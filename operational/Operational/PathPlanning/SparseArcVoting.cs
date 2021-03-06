using System;
using System.Collections.Generic;
using System.Text;
using OperationalLayer.Obstacles;
using UrbanChallenge.Common;
using UrbanChallenge.Common.Shapes;
using UrbanChallenge.Common.Vehicle;
using UrbanChallenge.Common.Sensors;
using UrbanChallenge.Operational.Common;
using OperationalLayer.Pose;
using OperationalLayer.CarTime;
using OperationalLayer.Tracking;
using OperationalLayer.Tracking.Steering;

namespace OperationalLayer.PathPlanning {
	static class SparseArcVoting {
		private const int num_arcs = 41;
		private const double max_curvature = 1/10.0; // minimum turning radius of 10 m

		private const double obstacle_weight = 10;
		private const double hysteresis_weight = 1;
		private const double straight_weight = 0.5;
		private const double goal_weight = 3;
		private const double side_obs_weight = 2;
		private const double roll_weight = 7;
		private const double road_weight = 6;

		public static ISteeringCommandGenerator SparcVote(ref double prevCurvature, Coordinates goalPoint) {
			double newCurvature = FindBestCurvature(prevCurvature, goalPoint);
			prevCurvature = newCurvature;
			if (double.IsNaN(newCurvature)) {
				return null;
			}
			else {
				return new ConstantSteeringCommandGenerator(SteeringUtilities.CurvatureToSteeringWheelAngle(newCurvature, 2), false);
			}
		}

		private static double FindBestCurvature(double prevCurvature, Coordinates goalPoint) {
			CarTimestamp curTimestamp = Services.RelativePose.CurrentTimestamp;

			AbsoluteTransformer absTransform = Services.StateProvider.GetAbsoluteTransformer();
			Coordinates relativeGoalPoint = absTransform.TransformPoint(goalPoint);

			// get a list of obstacles
			ObstacleCollection obstacles = Services.ObstaclePipeline.GetProcessedObstacles(curTimestamp, UrbanChallenge.Behaviors.SAUDILevel.None);
			List<Polygon> obstaclePolygons = new List<Polygon>();
			foreach (Obstacle obs in obstacles.obstacles){
				obstaclePolygons.Add(obs.cspacePolygon);
			}

			// get the side obstacles
			SideObstacle leftSideObstacle = Services.ObstaclePipeline.GetLeftSideObstacle();
			SideObstacle rightSideObstacle = Services.ObstaclePipeline.GetRightSideObstacle();

			double? leftDist = null, rightDist = null;
			if (leftSideObstacle != null) leftDist = leftSideObstacle.distance;
			if (rightSideObstacle != null) rightDist = rightSideObstacle.distance;

			double roll = Services.Dataset.ItemAs<double>("roll").CurrentValue;

			double roadBearing, roadConfidence;
			RoadBearing.GetCurrentData(out roadBearing, out roadConfidence);

			List<ArcResults> arcs = new List<ArcResults>();
			double maxUtility = double.MinValue;
			ArcResults selectedArc = null;

			// recalculate weights
			double totalWeights = obstacle_weight+hysteresis_weight+straight_weight+goal_weight+side_obs_weight+roll_weight+road_weight;
			double obstacleWeight = obstacle_weight/totalWeights;
			double hysteresisWeight = hysteresis_weight/totalWeights;
			double straightWeight = straight_weight/totalWeights;
			double goalWeight = goal_weight/totalWeights;
			double sideObsWeight = side_obs_weight/totalWeights;
			double rollWeight = roll_weight/totalWeights;
			double roadWeight = road_weight/totalWeights;

			int start = num_arcs/2;
			double curvatureStep = max_curvature/start;
			for (int i = -start; i <= start; i++) {
				double curvature = i*curvatureStep;

				double collisionDist, clearanceDist, collisionUtility;
				bool vetoed;
				EvaluateObstacleUtility(curvature, 20, obstaclePolygons, out collisionDist, out clearanceDist, out collisionUtility, out vetoed);

				double hystersisUtility = EvaluateHysteresisUtility(curvature, prevCurvature);
				double straightUtility = EvaluateStraightUtility(curvature);
				double goalUtility = EvaluateGoalUtility(curvature, relativeGoalPoint);
				double sideObstacleUtility = EvalualteSideObstacleUtility(curvature, leftDist, rightDist);
				double rollUtility = EvaluateRollUtility(curvature, roll);
				double roadUtility = EvaluateRoadBearingUtility(curvature, roadBearing, roadConfidence);

				double totalUtility = collisionUtility*obstacleWeight + hystersisUtility*hysteresisWeight + straightUtility*straightWeight +
					goalUtility*goalWeight + sideObstacleUtility*sideObsWeight + rollUtility*rollWeight + roadUtility*roadWeight;

				ArcResults result = new ArcResults();
				result.curvature = curvature;
				result.vetoed = vetoed;
				result.totalUtility = totalUtility;
				result.obstacleHitDistance = collisionDist;
				result.obstacleClearanceDistance = clearanceDist;
				result.obstacleUtility = collisionUtility;
				result.hysteresisUtility = hystersisUtility;
				result.straightUtility = straightUtility;
				result.goalUtility = goalUtility;
				result.sideObstacleUtility = sideObstacleUtility;
				result.rollUtility = rollUtility;
				result.roadUtility = roadUtility;

				arcs.Add(result);

				if (!vetoed && totalUtility > maxUtility) {
					maxUtility = totalUtility;
					selectedArc = result;
				}
			}

			ArcVotingResults results = new ArcVotingResults();
			results.arcResults = arcs;
			results.selectedArc = selectedArc;

			Services.Dataset.ItemAs<ArcVotingResults>("arc voting results").Add(results, LocalCarTimeProvider.LocalNow);

			if (selectedArc == null) {
				return double.NaN;
			}
			else {
				return selectedArc.curvature;
			}
		}

		private static void EvaluateObstacleUtility(double curvature, double dist, IList<Polygon> obstacles, out double collisionDist, out double clearanceDist, out double utility, out bool vetoed) {
			TestObstacleCollision(curvature, dist, obstacles, out collisionDist, out clearanceDist);
			vetoed = collisionDist<10;
			
			// evaluate utility based on distance -- cost is 0 at 20 m, 1 at 10 m
			const double distMin = 10;
			const double distMax = 20;

			double collisionUtility = (collisionDist-distMax)/(distMax - distMin);
			if (collisionUtility > 0) collisionUtility = 0;
			if (collisionUtility < -1) collisionUtility = -1;

			const double clearanceMin = 0;
			const double clearanceMax = 2;
			double clearanceUtility = (clearanceDist-clearanceMax)/(clearanceMax-clearanceMin);
			if (clearanceUtility > 0.2) clearanceUtility = 0.2;
			if (clearanceUtility < -1) clearanceUtility = -1;

			if (collisionUtility == 0) {
				utility = clearanceUtility;
			}
			else {
				utility = Math.Min(clearanceUtility, collisionUtility);
			}
		}

		private static void TestObstacleCollision(double curvature, double dist, IList<Polygon> obstacles, out double collisionDist, out double clearanceDist) {
			if (Math.Abs(curvature) < 1e-10) {
				// process as a straight line
				// determine end point of circle -- s = rθ, θ = sk (s = arc len, r = radius, θ = angle, k = curvature (1/r)
				// this will always be very very near straight, so just process as straight ahead
				LineSegment rearAxleSegment = new LineSegment(new Coordinates(0, 0), new Coordinates(dist, 0));
				collisionDist = TestObstacleCollisionStraight(rearAxleSegment, obstacles);
				clearanceDist = GetObstacleClearanceLine(rearAxleSegment, obstacles);
			}
			else {
				// build out the circle formed by the rear and front axle
				bool leftTurn = curvature > 0;
				double radius = Math.Abs(1/curvature);
				double frontRadius = Math.Sqrt(TahoeParams.FL*TahoeParams.FL + radius*radius);

				CircleSegment rearSegment, frontSegment;

				if (leftTurn) {
					Coordinates center = new Coordinates(0, radius);
					rearSegment = new CircleSegment(radius, center, Coordinates.Zero, dist, true);
					frontSegment = new CircleSegment(frontRadius, center, new Coordinates(TahoeParams.FL, 0), dist, true);
				}
				else {
					Coordinates center = new Coordinates(0, -radius);
					rearSegment = new CircleSegment(radius, center, Coordinates.Zero, dist, false);
					frontSegment = new CircleSegment(frontRadius, center, new Coordinates(TahoeParams.FL, 0), dist, false);
				}

				collisionDist = Math.Min(TestObstacleCollisionCircle(rearSegment, obstacles), TestObstacleCollisionCircle(frontSegment, obstacles));
				clearanceDist = Math.Min(GetObstacleClearanceCircle(rearSegment, obstacles), GetObstacleClearanceCircle(frontSegment, obstacles)); 
			}
		}

		private static double TestObstacleCollisionStraight(LineSegment segment, IList<Polygon> obstacles) {
			double minDist = double.MaxValue;
			foreach (Polygon obs in obstacles) {
				Coordinates[] pts;
				double[] K;
				if (obs.Intersect(segment, out pts, out K)) {
					// this is a potentially closest intersection
					for (int i = 0; i < K.Length; i++) {
						double dist = K[i]*segment.Length;
						if (dist < minDist) {
							minDist = dist;
						}
					}
				}
			}

			return minDist;
		}

		private static double TestObstacleCollisionCircle(CircleSegment segment, IList<Polygon> obstacles) {
			double minDist = double.MaxValue;
			foreach (Polygon obs in obstacles) {
				Coordinates[] pts;
				if (obs.Intersect(segment, out pts)) {
					for (int i = 0; i < pts.Length; i++) {
						// get the distance from the start
						double dist = segment.DistFromStart(pts[i]);
						if (dist < minDist) {
							minDist = dist;
						}
					}
				}
			}

			return minDist;
		}

		private static double GetObstacleClearanceLine(LineSegment segment, IList<Polygon> obstacles) {
			double minDist = double.MaxValue;
			foreach (Polygon obs in obstacles) {
				foreach (Coordinates pt in obs) {
					Coordinates closestPt = segment.ClosestPoint(pt);
					double dist = closestPt.DistanceTo(pt);
					if (dist < minDist)
						minDist = dist;
				}
			}

			return minDist;
		}

		private static double GetObstacleClearanceCircle(CircleSegment segment, IList<Polygon> obstacles) {
			double minDist = double.MaxValue;
			foreach (Polygon obs in obstacles) {
				foreach (Coordinates pt in obs) {
					Coordinates closestPt = segment.GetClosestPoint(pt);
					double dist = closestPt.DistanceTo(pt);
					if (dist < minDist)
						minDist = dist;
				}
			}

			return minDist;
		}

		private static double EvaluateRollUtility(double curvature, double roll) {
			// for positive roll, want to turn right (so negative curvature)
			// calculate a [0,1] cost for roll, assuming a minimum and maximum roll angle
			const double minRollAngle = 2*Math.PI/180;
			const double maxRollAngle = 6*Math.PI/180;

			double scaledRoll = (Math.Abs(roll) - minRollAngle)/(maxRollAngle-minRollAngle);
			if (scaledRoll > 1) scaledRoll = 1;
			if (scaledRoll < 0) scaledRoll = 0;

			if (scaledRoll == 0) return 0;

			// flag indicating if we want to avoid turning to the left
			bool avoidLeft = roll > 0;

			// mark all turns to that go the wrong way with the scaled roll penalty
			if (avoidLeft) {
				curvature = -curvature;
			}

			if (curvature <= 0) {
				// mark with the negative scaled roll cost
				return -scaledRoll;
			}
			else {
				// scale such that for every 0.01 of curvature, we drop the roll penalty by 0.1;
				double scaleFactor = curvature/0.01;
				double turnUtility = -scaledRoll + scaleFactor*0.3;
				// cap the utility at 0.5
				if (turnUtility > scaledRoll) turnUtility = scaledRoll;

				return turnUtility;
			}
		}

		private static double EvalualteSideObstacleUtility(double curvature, double? leftDist, double? rightDist) {
			if (leftDist == null && rightDist == null) {
				return 0;
			}
			else if (leftDist.HasValue != rightDist.HasValue) {
				//// there is either an obstacle to the left or right but not both
				//double dist;
				//bool avoidLeft;
				//if (leftDist.HasValue) {
				//  dist = leftDist.Value;
				//  avoidLeft = true;
				//}
				//else {
				//  dist = rightDist.Value;
				//  avoidLeft = false;
				//}

				//// we have the distance and which way we don't want to turn
				//// create a scaled cost [-1,0] depending on the distance to the obstacle
				//const double distMin = 1;
				//const double distMax = 2.5;
				//double scaledOffsetCost = (Math.Abs(dist) - distMin)/(distMax - distMin);
				//if (scaledOffsetCost > 1) scaledOffsetCost = 1;
				//if (scaledOffsetCost < 0) scaledOffsetCost = 0;
				//// make it so that being at the min dist has a cost of -1, being at the max dist has a cost of 0
				//scaledOffsetCost = scaledOffsetCost - 1;

				//// if we want to avoid going to the left, reverse the curvature so that negative is to the left
				//if (avoidLeft) {
				//  curvature = -curvature;
				//}

				//// set it so that going straight has the scaled offset cost and decrease by 0.1 for every 0.01 curvature
				//double scaleFactor = curvature/0.01;

				//double turnCost = scaledOffsetCost + scaleFactor*0.1;
				//// cap the utility at 0 and -1
				//if (turnCost > 0) turnCost = 0;
				//if (turnCost < -1) turnCost = -1;

				//return turnCost;
				return 0;
			}
			else {
				if (Math.Abs(leftDist.Value) > 10 || Math.Abs(rightDist.Value) > 10)
					return 0;

				// both have a distance
				// calculate the "center" of the lane
				double center = (leftDist.Value-rightDist.Value)/2.0;

				if (Math.Abs(center) > 2) {
					return 0;
				}

				// create a target point 10 m out from the vehicle and use pure-pursuit to get a target curvature
				Coordinates targetPoint = new Coordinates(10, center);
				double targetCurvature = GetPurePursuitCurvature(targetPoint);

				// compute a squared scale factor
				double scaleFactor = Math.Pow((curvature - targetCurvature)/0.02, 2);

				// return a value of 1 - scaleFactor*0.1 for the turn
				double turnUtility = 1 - scaleFactor*0.1;
				if (turnUtility < -0.75) turnUtility = -0.75;

				return turnUtility;
			}
		}

		private static double EvaluateGoalUtility(double curvature, Coordinates relativeGoalPoint) {
			// get the angle to the goal point
			double angle = relativeGoalPoint.ArcTan;

			const double angleMax = 45*Math.PI/180.0;
			double scaledAngle = angle/angleMax;
			if (scaledAngle > 1) scaledAngle = 1;
			if (scaledAngle < -1) scaledAngle = -1;

			// calculate the target curvature to hit the goal
			double targetCurvature = scaledAngle*max_curvature;

			// calculate a matching scale factor
			double scaleFactor = Math.Pow((curvature - targetCurvature)/0.01, 2);

			// calculate a distance weighting factor
			double distMin = 20;
			double distMax = 70;
			double distFactor = (relativeGoalPoint.Length - distMin)/(distMax - distMin);
			if (distFactor > 0.6) distFactor = 0.6;
			if (distFactor < 0) distFactor = 0;
			distFactor = 1-distFactor;

			double turnFactor = (1-scaleFactor*0.1);
			if (turnFactor < -1) turnFactor = -1;

			return turnFactor*distFactor;
		}

		private static double EvaluateHysteresisUtility(double curvature, double prevCurvature) {
			if (double.IsNaN(prevCurvature)) {
				return 0;
			}

			double scaleFactor = Math.Pow((curvature-prevCurvature)/0.01, 2);

			double turnFactor = 1-scaleFactor*0.1;
			if (turnFactor < 0) turnFactor = 0;
			return turnFactor;
		}

		private static double EvaluateStraightUtility(double curvature) {
			double scaleFactor = Math.Pow(curvature/0.02, 2);
			double turnFactor = 1-scaleFactor*0.5;
			if (turnFactor < 0) turnFactor = 0;
			return turnFactor;
		}

		private static double EvaluateRoadBearingUtility(double curvature, double bearing, double confidence) {
			// create a scaled angle
			const double angleMax = 45*Math.PI/180;
			// scale to be [-1,1]
			double scaledAngle = bearing/angleMax;
			
			// figure out the peak curvature from the scaled bearing
			double peakCurvature = max_curvature*scaledAngle;

			double scaleFactor = Math.Pow((curvature-peakCurvature)/0.02, 2);

			double turnFactor = 1-scaleFactor*1;
			if (turnFactor < -1) turnFactor = -1;

			if (confidence < 0) confidence = 0;
			if (confidence > 1) confidence = 1;

			turnFactor *= confidence;

			return turnFactor;
		}

		/// <summary>
		/// Returns the curvature we would need to take pass through the target point
		/// </summary>
		/// <param name="targetPoint">Point to hit in vehicle relative coordinates</param>
		/// <returns>Target curvature</returns>
		private static double GetPurePursuitCurvature(Coordinates targetPoint) {
			return 2*targetPoint.Y/targetPoint.VectorLength2;
		}
	}
}
