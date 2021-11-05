using PersistentThrust.BackgroundProcessing;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PersistentThrust
{
    public static class VesselExtension
    {
        private static bool hasPersistentEngineModules;

        public static float GetDryMass(this Vessel vessel)
        {
            float dryMass = 0f;

            foreach (var part in vessel.parts)
            {
                dryMass += part.mass;
            }

            return dryMass;
        }

        public static List<ProtoPartModuleSnapshot> FindPersistentEngineModuleSnapshots(this Vessel vessel)
        {
            var list = new List<ProtoPartModuleSnapshot>();
            if (vessel.loaded)
            {
                return list;
            }
            else
            {
                foreach (var protoPart in vessel.protoVessel.protoPartSnapshots)
                {
                    ProtoPartModuleSnapshot moduleSnapshot = protoPart.FindModule(nameof(PersistentEngine));

                    if (moduleSnapshot is null)
                        continue;

                    list.Add(moduleSnapshot);
                }

                return list;
            }
        }

        public static bool HasPersistentEngineModules(this Vessel vessel)
        {
            if (vessel.loaded)
            {
                var pe = vessel.FindPartModuleImplementing<PersistentEngine>();
                hasPersistentEngineModules = !(pe is null);
            }
            else
            {
                hasPersistentEngineModules = FindPersistentEngineModuleSnapshots(vessel).Any();
            }

            return hasPersistentEngineModules;
        }

        public static VesselAutopilot.AutopilotMode GetAutopilotMode(this Vessel vessel)
        {
            if (vessel.loaded)
            {
                return vessel.Autopilot.Mode;
            }
            else
            {
                PersistentScenarioModule.VesselDataDict.TryGetValue(vessel.id, out VesselData vesselModule);

                if (vesselModule != null)
                    return vesselModule.PersistentAutopilotMode;
            }

            return VesselAutopilot.AutopilotMode.StabilityAssist;
        }

        public static bool IsVesselSituationValid(this Vessel v)
        {
            return v.situation == Vessel.Situations.ORBITING || v.situation == Vessel.Situations.ESCAPING || v.situation == Vessel.Situations.SUB_ORBITAL;
        }

        public static bool IsVesselValid(this Vessel v)
        {
            return !(v.vesselType == VesselType.SpaceObject || !v.isCommandable || v.isEVA);
        }

        public static double PersistHeading(this Vessel vessel, float fixedDeltaTime, float headingTolerance = 0.001f, bool forceRotation = false, bool canDropOutOfTimeWarp = true)
        {
            if (!vessel.packed && !forceRotation)
                return 0;

            var canPersistDirection = vessel.situation == Vessel.Situations.SUB_ORBITAL || vessel.situation == Vessel.Situations.ESCAPING || vessel.situation == Vessel.Situations.ORBITING;

            if (!canPersistDirection)
                return 0;

            var sasIsActive = vessel.ActionGroups[KSPActionGroup.SAS];

            if (!sasIsActive)
                return 0;

            var requestedDirection = GetRequestedDirection(vessel, Planetarium.GetUniversalTime());

            if (requestedDirection == Vector3d.zero)
                return 1;

            var ratioHeadingVersusRequest = Vector3d.Dot(vessel.transform.up.normalized, requestedDirection);

            var finalTolerance = Math.Min(0.995, (1 - Math.Min(1, fixedDeltaTime * headingTolerance)));

            if (forceRotation || ratioHeadingVersusRequest > finalTolerance)
            {
                vessel.transform.Rotate(Quaternion.FromToRotation(vessel.transform.up.normalized, requestedDirection).eulerAngles, Space.World);
                vessel.SetRotation(vessel.transform.rotation);
                return 1;
            }
            else if (vessel.ctrlState.mainThrottle == 0 || canDropOutOfTimeWarp == false)
            {
                return ratioHeadingVersusRequest;
            }
            else
            {
                var directionName = Enum.GetName(typeof(VesselAutopilot.AutopilotMode), vessel.Autopilot.Mode);
                var message = "Persistent Thrust stopped - vessel is not facing " + directionName;
                ScreenMessages.PostScreenMessage(message, 5, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log("[PersistentThrust]: " + message);
                TimeWarp.SetRate(0, true);

                return ratioHeadingVersusRequest;
            }
        }

        public static double HeadingVersusAutopilotVector(this Vessel vessel, double universalTime)
        {
            var requestedDirection = GetRequestedDirection(vessel, universalTime);

            return Vector3d.Dot(vessel.transform.up.normalized, requestedDirection);
        }

        public static Vector3d GetFacing(Vessel vessel)
        {
            var vesselRotation = vessel.ReferenceTransform.rotation;
            Quaternion vesselFacing = Quaternion.Inverse(Quaternion.Euler(90, 0, 0) * Quaternion.Inverse(vesselRotation) * Quaternion.identity);

            var euler = vesselFacing.eulerAngles;
            var vector = vesselFacing * Vector3d.forward;
            return vector;
        }

        public static double GetVesselOrbitHeadingVersusManeuverVector(this Vessel vessel)
        {
            if (vessel == null || vessel.patchedConicSolver == null || vessel.orbit == null || vessel.patchedConicSolver.maneuverNodes == null)
                return 1;

            if (vessel.patchedConicSolver.maneuverNodes.Count > 0)
            {
                var maneuverNode = vessel.patchedConicSolver.maneuverNodes[0];

                if (maneuverNode == null)
                    return -1;

                var facing = GetFacing(vessel);
                var burnVector = vessel.orbit.GetBurnVector(maneuverNode.nextPatch, maneuverNode.patch, maneuverNode.UT).normalized;

                //return vessel.orbit.GetVesselOrbitHeadingVersusManeuverVector(maneuverNode.nextPatch, maneuverNode.patch, maneuverNode.UT);
                return Vector3d.Dot(facing, burnVector);
            }
            else
                return 1;
        }


        public static Vector3d GetRequestedDirection(this Vessel vessel, double universalTime)
        {
            var requestedDirection = Vector3d.zero;
            var vesselPosition = vessel.orbit.getPositionAtUT(universalTime);

            switch (vessel.Autopilot.Mode)
            {
                case VesselAutopilot.AutopilotMode.Prograde:
                    requestedDirection = vessel.obt_velocity.normalized;
                    break;
                case VesselAutopilot.AutopilotMode.Retrograde:
                    requestedDirection = -vessel.obt_velocity.normalized;
                    break;
                case VesselAutopilot.AutopilotMode.Maneuver:
                    requestedDirection = vessel.patchedConicSolver.maneuverNodes.Count > 0 ? vessel.patchedConicSolver.maneuverNodes[0].GetBurnVector(vessel.orbit).normalized : vessel.obt_velocity.normalized;
                    break;
                case VesselAutopilot.AutopilotMode.Target:
                    requestedDirection = (vessel.targetObject.GetOrbit().getPositionAtUT(universalTime) - vesselPosition).normalized;
                    break;
                case VesselAutopilot.AutopilotMode.AntiTarget:
                    requestedDirection = -(vessel.targetObject.GetOrbit().getPositionAtUT(universalTime) - vesselPosition).normalized;
                    break;
                case VesselAutopilot.AutopilotMode.Normal:
                    requestedDirection = Vector3.Cross(vessel.obt_velocity, vesselPosition - vessel.mainBody.position).normalized;
                    break;
                case VesselAutopilot.AutopilotMode.Antinormal:
                    requestedDirection = -Vector3.Cross(vessel.obt_velocity, vesselPosition - vessel.mainBody.position).normalized;
                    break;
                case VesselAutopilot.AutopilotMode.RadialIn:
                    requestedDirection = -Vector3.Cross(vessel.obt_velocity, Vector3.Cross(vessel.obt_velocity, vesselPosition - vessel.mainBody.position)).normalized;
                    break;
                case VesselAutopilot.AutopilotMode.RadialOut:
                    requestedDirection = Vector3.Cross(vessel.obt_velocity, Vector3.Cross(vessel.obt_velocity, vesselPosition - vessel.mainBody.position)).normalized;
                    break;
            }

            return requestedDirection;
        }
    }
}
