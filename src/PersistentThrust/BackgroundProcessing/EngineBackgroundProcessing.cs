using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PersistentThrust.BackgroundProcessing
{
    public static class EngineBackgroundProcessing
    {
        public static PersistentEngineData LoadPersistentEngine(int partIndex, ProtoPartSnapshot protoPartSnapshot,
            VesselData vesselData, Part protoPart)
        {
            var persistentEngine = protoPart?.FindModuleImplementing<PersistentEngine>();

            if (persistentEngine is null)
                return null;

            // find ProtoPartModuleSnapshot and moduleIndex
            int moduleIndex;
            ProtoPartModuleSnapshot persistentEngineModuleSnapshot = null;
            for (moduleIndex = 0; moduleIndex < protoPartSnapshot.modules.Count; moduleIndex++)
            {                
                if (protoPartSnapshot.modules[moduleIndex].moduleName == nameof(PersistentEngine))
                {
                    persistentEngineModuleSnapshot = protoPartSnapshot.modules[moduleIndex];
                    break;
                }
            }

            if (persistentEngineModuleSnapshot == null)
                return null;

            var engineData = new PersistentEngineData
            {
                PartIndex = partIndex,
                ModuleIndex = moduleIndex,
                ProtoPart = protoPart,
                ProtoPartSnapshot = protoPartSnapshot,
                PersistentPartId = protoPartSnapshot.persistentId,
                ProtoPartModuleSnapshot = persistentEngineModuleSnapshot,
                PersistentEngine = persistentEngine
            };

            // store data
            vesselData.Engines.Add(protoPartSnapshot.persistentId, engineData);

            // Load persistentThrust from ProtoPartModuleSnapshots
            persistentEngineModuleSnapshot.moduleValues.TryGetValue(nameof(persistentEngine.persistentThrust),
                ref persistentEngine.persistentThrust);

            var moduleEngines = protoPart.FindModulesImplementing<ModuleEngines>();

            if (moduleEngines == null || moduleEngines.Any() == false)
                return null;

            // collect propellant configurations
            var persistentEngineModules = new List<PersistentEngineModule>();
            foreach (var moduleEngine in moduleEngines)
            {
                engineData.MaxThrust += moduleEngine.maxThrust;

                List<PersistentPropellant> persistentPropellants = PersistentPropellant.MakeList(moduleEngine.propellants);

                var persistentEngineModule = new PersistentEngineModule
                {
                    propellants = persistentPropellants,
                    averageDensity = persistentPropellants.AverageDensity()
                };
                persistentEngineModules.Add(persistentEngineModule);
            }

            persistentEngine.moduleEngines = persistentEngineModules.ToArray();

            // check if there any active persistent engines that need to be processed
            if (persistentEngine.persistentThrust > 0)
                vesselData.HasAnyActivePersistentEngine = true;
            else
                return null;

            return engineData;
        }

        public static void ProcessUnloadedPersistentEngines(VesselData vesselData, double elapsedTime)
        {
            // ignore landed or floating vessels
            if (vesselData.Vessel.LandedOrSplashed)
                return;

            vesselData.PersistentThrust = 0;

            foreach (KeyValuePair<uint, PersistentEngineData> keyValuePair in vesselData.Engines)
            {
                PersistentEngineData persistentEngineData = keyValuePair.Value;

                // update snapshots
                persistentEngineData.ProtoPartSnapshot = vesselData.Vessel.protoVessel.protoPartSnapshots[persistentEngineData.PartIndex];
                persistentEngineData.ProtoPartModuleSnapshot = persistentEngineData.ProtoPartSnapshot.modules[persistentEngineData.ModuleIndex];

                // update persistentThrust
                double.TryParse(persistentEngineData.ProtoPartModuleSnapshot.moduleValues.GetValue(nameof(persistentEngineData.PersistentEngine.persistentThrust)), out persistentEngineData.PersistentEngine.persistentThrust);
                if (persistentEngineData.PersistentEngine.persistentThrust <= 0)
                    continue;

                float.TryParse(persistentEngineData.ProtoPartModuleSnapshot.moduleValues.GetValue(nameof(persistentEngineData.PersistentEngine.maxThrust)), out persistentEngineData.PersistentEngine.maxThrust);
                if (persistentEngineData.PersistentEngine.maxThrust <= 0)
                    persistentEngineData.PersistentEngine.maxThrust = (float)persistentEngineData.PersistentEngine.persistentThrust;

                vesselData.PersistentThrust += persistentEngineData.PersistentEngine.persistentThrust;

                var resourceChangeRequests = new List<KeyValuePair<string, double>>();

                ProcessUnloadedPersistentEngine(persistentEngineData.ProtoPartSnapshot, vesselData, resourceChangeRequests, elapsedTime);

                foreach (KeyValuePair<string, double> resourceChangeRequest in resourceChangeRequests)
                {
                    vesselData.ResourceChange(resourceChangeRequest.Key, resourceChangeRequest.Value);
                }
            }
        }

        private static void ProcessUnloadedPersistentEngine(
            ProtoPartSnapshot protoPartSnapshot,
            VesselData vesselData,
            List<KeyValuePair<string, double>> resourceChangeRequest,
            double elapsedTime)
        {
            if (DetectKerbalism.Found)
                return;

            // lookup engine data
            vesselData.Engines.TryGetValue(protoPartSnapshot.persistentId, out PersistentEngineData engineData);

            if (engineData == null)
            {
                Debug.LogWarning("[PersistentThrust]: Fail to find Engine Data for persistentId " + protoPartSnapshot.persistentId);
                return;
            }

            // execute persistent engine BackgroundUpdate
            PersistentEngine.BackgroundUpdate(
                vessel: vesselData.Vessel,
                part_snapshot: protoPartSnapshot,
                module_snapshot: engineData.ProtoPartModuleSnapshot,
                proto_part_module: engineData.PersistentEngine,
                proto_part: engineData.ProtoPart,
                availableResources: vesselData.AvailableResources,
                resourceChangeRequest: resourceChangeRequest,
                elapsed_s: elapsedTime);
        }

        public static string BackgroundUpdateExecution(
            Vessel vessel,
            ProtoPartSnapshot part_snapshot,
            ProtoPartModuleSnapshot module_snapshot,
            PartModule proto_part_module,
            Part proto_part,
            Dictionary<string, double> availableResources,
            List<KeyValuePair<string, double>> resourceChangeRequest,
            double elapsed_s)
        {

            bool HasPersistentThrust = bool.Parse(module_snapshot.moduleValues.GetValue(nameof(HasPersistentThrust)));
            if (!HasPersistentThrust)
                return proto_part.partInfo.title;

            double persistentThrust = 0;
            if (!module_snapshot.moduleValues.TryGetValue(nameof(persistentThrust), ref persistentThrust))
                return proto_part.partInfo.title;

            double persistentThrottle = 0;
            if (!module_snapshot.moduleValues.TryGetValue(nameof(persistentThrottle), ref persistentThrottle))
                return proto_part.partInfo.title;

            double requestedThrust = persistentThrust * persistentThrottle;

            // ignore background update when no thrust generated
            if (requestedThrust <= 0)
                return proto_part.partInfo.title;

            double vesselAlignmentWithAutopilotMode = 0;
            if (!module_snapshot.moduleValues.TryGetValue(nameof(vesselAlignmentWithAutopilotMode),
                ref vesselAlignmentWithAutopilotMode))
                return proto_part.partInfo.title;

            // ignore background update when not aligned with autopilot mode
            if (vesselAlignmentWithAutopilotMode < 0.995)
                return proto_part.partInfo.title;

            string persistentResourceChange = module_snapshot.moduleValues.GetValue(nameof(persistentResourceChange));

            if (string.IsNullOrEmpty(persistentResourceChange))
                return proto_part.partInfo.title;

            Dictionary<string, double> resourceChanges = persistentResourceChange
                .Split(';').Select(s => s.Trim().Split('='))
                .ToDictionary(a => a[0], a => double.Parse(a[1]));

            if (resourceChanges.Any(m => m.Value == 0))
                return proto_part.partInfo.title;


            PersistentScenarioModule.VesselDataDict.TryGetValue(vessel.id, out VesselData vesselData);
            if (vesselData == null)
                return proto_part.partInfo.title;

            // read persistent fields
            module_snapshot.moduleValues.TryGetValue(nameof(vesselData.persistentVesselTargetBodyName), ref vesselData.persistentVesselTargetBodyName);
            module_snapshot.moduleValues.TryGetValue(nameof(vesselData.persistentVesselTargetId), ref vesselData.persistentVesselTargetId);
            module_snapshot.moduleValues.TryGetValue(nameof(vesselData.persistentManeuverUT), ref vesselData.persistentManeuverUT);
            module_snapshot.moduleValues.TryGetValue(nameof(vesselData.maneuverToleranceInDegree), ref vesselData.maneuverToleranceInDegree);
            module_snapshot.moduleValues.TryGetValue(nameof(vesselData.persistentManeuverNextPatch), ref vesselData.persistentManeuverNextPatch);

            if (vesselData.HeadingVector == Vector3d.zero)
                return proto_part.partInfo.title;

            double fuelRequirementMet = 1;
            foreach (var resourceChange in resourceChanges)
            {
                var fixedRequirement = -resourceChange.Value * elapsed_s;

                if (availableResources.TryGetValue(resourceChange.Key, out double availableAmount))
                {
                    var resourceDefinition = PartResourceLibrary.Instance.GetDefinition(resourceChange.Key);

                    if ((resourceDefinition.density <= 0 && CheatOptions.InfiniteElectricity) ||
                        resourceDefinition.density > 0 && CheatOptions.InfinitePropellant)
                        fuelRequirementMet = 1;
                    else
                        fuelRequirementMet = availableAmount > 0 && fixedRequirement > 0
                            ? Math.Min(fuelRequirementMet, availableAmount / fixedRequirement)
                            : 0;
                }
                else
                    fuelRequirementMet = 0;
            }

            if (fuelRequirementMet <= 0)
                return proto_part.partInfo.title;

            foreach (var resourceChange in resourceChanges)
            {
                var resourceDefinition = PartResourceLibrary.Instance.GetDefinition(resourceChange.Key);

                if ((resourceDefinition.density <= 0 && CheatOptions.InfiniteElectricity) ||
                    resourceDefinition.density > 0 && CheatOptions.InfinitePropellant)
                    continue;

                resourceChangeRequest.Add(new KeyValuePair<string, double>(resourceChange.Key,
                    resourceChange.Value * fuelRequirementMet));
            }

            double persistentAverageDensity = 0;
            if (!module_snapshot.moduleValues.TryGetValue(nameof(persistentAverageDensity),
                ref persistentAverageDensity))
                return proto_part.partInfo.title;

            float persistentIsp = 0;
            if (!module_snapshot.moduleValues.TryGetValue(nameof(persistentIsp), ref persistentIsp))
                return proto_part.partInfo.title;

            vesselData.Engines.TryGetValue(part_snapshot.persistentId, out PersistentEngineData engineData);
            if (engineData == null)
                return proto_part.partInfo.title;

            engineData.DeltaV = Utils.CalculateDeltaV(vesselData.TotalVesselMassInTon, elapsed_s,
                requestedThrust * fuelRequirementMet, persistentIsp);

            return proto_part.partInfo.title;
        }

        public static Vector3d GetHeadingVectorForAutoPilot(VesselData vesselData, double UT)
        {
            Vector3d headingVector = Vector3d.zero;
            switch (vesselData.PersistentAutopilotMode)
            {
                case VesselAutopilot.AutopilotMode.StabilityAssist:
                    headingVector = vesselData.Vessel.GetTransform().up.normalized;
                    break;
                case VesselAutopilot.AutopilotMode.Prograde:
                    headingVector = vesselData.OrbitalVelocityAtUt;
                    break;
                case VesselAutopilot.AutopilotMode.Retrograde:
                    headingVector = -vesselData.OrbitalVelocityAtUt;
                    break;
                case VesselAutopilot.AutopilotMode.Normal:
                    headingVector = Vector3.Cross(vesselData.OrbitalVelocityAtUt,
                        vesselData.Orbit.getPositionAtUT(UT) - vesselData.Vessel.mainBody.getPositionAtUT(UT));
                    break;
                case VesselAutopilot.AutopilotMode.Antinormal:
                    headingVector = -Vector3.Cross(vesselData.OrbitalVelocityAtUt,
                        vesselData.Orbit.getPositionAtUT(UT) - vesselData.Vessel.mainBody.getPositionAtUT(UT));
                    break;
                case VesselAutopilot.AutopilotMode.RadialIn:
                    headingVector = -Vector3.Cross(vesselData.OrbitalVelocityAtUt,
                        Vector3.Cross(vesselData.OrbitalVelocityAtUt,
                            vesselData.Orbit.getPositionAtUT(UT) - vesselData.Orbit.referenceBody.position));
                    break;
                case VesselAutopilot.AutopilotMode.RadialOut:
                    headingVector = Vector3.Cross(vesselData.OrbitalVelocityAtUt,
                        Vector3.Cross(vesselData.OrbitalVelocityAtUt,
                            vesselData.Orbit.getPositionAtUT(UT) - vesselData.Orbit.referenceBody.position));
                    break;
                case VesselAutopilot.AutopilotMode.Target:
                    headingVector = GetHeadingVectorToTarget(vesselData.Vessel, UT, vesselData.persistentVesselTargetBodyName, vesselData.persistentVesselTargetId);
                    break;
                case VesselAutopilot.AutopilotMode.AntiTarget:
                    headingVector = -GetHeadingVectorToTarget(vesselData.Vessel, UT, vesselData.persistentVesselTargetBodyName, vesselData.persistentVesselTargetId);
                    break;
                case VesselAutopilot.AutopilotMode.Maneuver:
                    headingVector = vesselData.Orbit.GetThrustVectorToManeuver(vesselData.persistentManeuverNextPatch, vesselData.persistentManeuverPatch, vesselData.persistentManeuverUT, vesselData.maneuverToleranceInDegree);
                    break;
            }

            return headingVector;
        }

        private static Vector3d GetHeadingVectorToTarget(Vessel vessel, double UT, string persistentVesselTargetBodyName, string persistentVesselTargetId)
        {
            Guid persistentVesselTargetGuid = new Guid(persistentVesselTargetId);

            Orbit targetOrbit = null;
            if (persistentVesselTargetGuid != Guid.Empty)
            {
                Vessel targetVessel = FlightGlobals.Vessels.SingleOrDefault(m => m.id == persistentVesselTargetGuid);
                if (targetVessel != null)
                    targetOrbit = targetVessel.GetOrbit();
            }
            else if (!string.IsNullOrEmpty(persistentVesselTargetBodyName))
            {
                CelestialBody body = FlightGlobals.Bodies.SingleOrDefault(m => m.bodyName == persistentVesselTargetBodyName);
                if (body != null)
                    targetOrbit = body.GetOrbit();
            }

            return targetOrbit != null ? targetOrbit.getPositionAtUT(UT) - vessel.orbit.getPositionAtUT(UT) : Vector3d.zero;
        }
    }
}
