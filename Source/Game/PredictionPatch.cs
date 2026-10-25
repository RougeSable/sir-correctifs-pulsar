using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;

namespace SirCorrectifsPulsar
{
    // The patch on MyCubeGrid.UpdatePredictionFlag, where the game decides
    // whether the grid the player pilots is simulated on his machine.
    //
    // The game refuses it as soon as the piloted grid carries another grid
    // (MyGridPhysicalHierarchy.HasChildren), and only makes an exception for a
    // pure wheeled vehicle, behind its own wheel prediction switch, in a
    // branch that skips its safeguards. The transpiler replaces that single
    // question by HasChildren below: a grid that only carries wheels (setting
    // 1) and grids attached by rotors, pistons and hinges (setting 2) is
    // answered "no children". The game's main branch then decides, with all
    // its safeguards: anchored to a station, locked connector, too many
    // predicted contacts, prediction disabled by the server.
    //
    // The postfix makes the attached grids follow: the game gives an attached
    // grid the prediction of its root, but only recomputes it on rare events.
    // When they differ, the attached grids are recomputed by the game's own
    // method, on the main thread.
    //
    // Both settings off, the hook answers exactly what the game answers and
    // the postfix does nothing.
    internal static class PredictionPatch
    {
        // Deeper than any real construction: a guard against a cyclic
        // hierarchy, never reached in practice.
        private const int MaxDepth = 32;

        // What the plugin does right now, decided on the main thread
        // (GameSwitch.Wanted), read on the game's parallel threads.
        private static volatile bool s_wheels;
        private static volatile bool s_mechanical;
        private static int s_refreshQueued;

        public static GameNames Names;
        public static SessionStop Stop;

        public static bool Wheels
        {
            get { return s_wheels; }
        }

        public static bool Mechanical
        {
            get { return s_mechanical; }
        }

        public static void Set(bool wheels, bool mechanical)
        {
            s_wheels = wheels;
            s_mechanical = mechanical;
        }

        // How many times the original method asks HasChildren, null if its
        // body cannot be read. The patch is placed only if it is exactly once,
        // as in the game we read.
        public static int? CountHasChildrenCalls(MethodBase original, MethodInfo hasChildren)
        {
            return MethodCalls.Count(original, hasChildren);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var hook = AccessTools.Method(typeof(PredictionPatch), "HasChildren");
            foreach (var instruction in instructions)
            {
                // The instance call becomes a static call taking the same two
                // values from the stack: the hierarchy, then the grid. Labels
                // and blocks of the instruction are kept.
                if (instruction.Calls(Names.HasChildren))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = hook;
                }
                yield return instruction;
            }
        }

        // Called by the game instead of MyGridPhysicalHierarchy.HasChildren,
        // for the root of a hierarchy piloted by a player, on a parallel
        // update thread.
        public static bool HasChildren(MyGridPhysicalHierarchy hierarchy, MyCubeGrid grid)
        {
            var hasChildren = hierarchy.HasChildren(grid);
            var wheels = s_wheels;
            var mechanical = s_mechanical;
            if (!hasChildren || (!wheels && !mechanical))
                return hasChildren;

            try
            {
                var attachments = new List<Attachment>();
                Collect(hierarchy, grid, attachments, new HashSet<long> { grid.EntityId }, 0);
                if (!AttachedGrids.LiftsTheRule(attachments, wheels, mechanical))
                    return hasChildren;

                // A rover, or a vehicle with wheels and, say, a turret on a
                // rotor: its wheels get the game's wheel profile, the vehicle
                // its car profile, as in the game's own wheel prediction.
                if (wheels && attachments.Contains(Attachment.Wheel))
                    MarkWheels(hierarchy, grid);
                return false;
            }
            catch (Exception e)
            {
                Fault(e);
                return hasChildren;
            }
        }

        public static void Postfix(MyCubeGrid __instance)
        {
            if ((!s_wheels && !s_mechanical) || Stop == null || Stop.IsStopped)
                return;

            try
            {
                var hierarchy = MyGridPhysicalHierarchy.Static;
                if (hierarchy == null || hierarchy.GetRoot(__instance) != __instance || !hierarchy.HasChildren(__instance))
                    return;
                if (ChildrenFollow(hierarchy, __instance, __instance.IsClientPredicted, new HashSet<long> { __instance.EntityId }, 0))
                    return;
                QueueRefresh(__instance);
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        public static Attachment Classify(MyEntity link, MyCubeGrid child)
        {
            if (link is MyMotorSuspension)
                return HasWheel(child) ? Attachment.Wheel : Attachment.Other;
            if (link is MyMechanicalConnectionBlockBase)
                return Attachment.Mechanical;
            return Attachment.Other;
        }

        private static void Collect(MyGridPhysicalHierarchy hierarchy, MyCubeGrid grid, List<Attachment> attachments, HashSet<long> seen, int depth)
        {
            if (depth >= MaxDepth)
            {
                attachments.Add(Attachment.Other);
                return;
            }

            hierarchy.ApplyOnChildren(grid, child =>
            {
                if (!seen.Add(child.EntityId))
                {
                    attachments.Add(Attachment.Other);
                    return;
                }
                attachments.Add(Classify(hierarchy.GetEntityConnectingToParent(child), child));

                // The game only looks at the connectors of the piloted grid:
                // an attached grid docked somewhere keeps the refusal too.
                if (HasLockedConnector(child))
                    attachments.Add(Attachment.Other);

                Collect(hierarchy, child, attachments, seen, depth + 1);
            });
        }

        private static void MarkWheels(MyGridPhysicalHierarchy hierarchy, MyCubeGrid root)
        {
            var car = false;
            var seen = new HashSet<long> { root.EntityId };
            var pending = new Queue<KeyValuePair<MyCubeGrid, int>>();
            pending.Enqueue(new KeyValuePair<MyCubeGrid, int>(root, 0));
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (current.Value >= MaxDepth)
                    continue;
                hierarchy.ApplyOnChildren(current.Key, child =>
                {
                    if (!seen.Add(child.EntityId))
                        return;
                    if (Classify(hierarchy.GetEntityConnectingToParent(child), child) == Attachment.Wheel)
                    {
                        Names.SetPredictedWheel(child, true);
                        if (current.Key == root)
                            car = true;
                    }
                    pending.Enqueue(new KeyValuePair<MyCubeGrid, int>(child, current.Value + 1));
                });
            }
            if (car)
                Names.SetPredictedCar(root, true);
        }

        private static bool HasWheel(MyCubeGrid grid)
        {
            foreach (var block in grid.GetFatBlocks())
            {
                if (block is MyWheel)
                    return true;
            }
            return false;
        }

        private static bool HasLockedConnector(MyCubeGrid grid)
        {
            foreach (var connector in grid.GetFatBlocks<MyShipConnector>())
            {
                if (connector.InConstraint)
                    return true;
            }
            return false;
        }

        // True when every attached grid already has the prediction the game
        // gives it from its root.
        private static bool ChildrenFollow(MyGridPhysicalHierarchy hierarchy, MyCubeGrid grid, bool rootPredicted, HashSet<long> seen, int depth)
        {
            if (depth >= MaxDepth)
                return true;

            var follow = true;
            hierarchy.ApplyOnChildren(grid, child =>
            {
                if (!follow || !seen.Add(child.EntityId))
                    return;
                if (child.InScene && child.Physics != null)
                {
                    var expected = rootPredicted && !child.IsStatic && !child.ForceDisablePrediction && child.AllowPrediction;
                    if (child.IsClientPredicted != expected)
                    {
                        follow = false;
                        return;
                    }
                }
                if (!ChildrenFollow(hierarchy, child, rootPredicted, seen, depth + 1))
                    follow = false;
            });
            return follow;
        }

        private static void QueueRefresh(MyCubeGrid root)
        {
            if (Interlocked.Exchange(ref s_refreshQueued, 1) != 0)
                return;

            MyEntities.InvokeLater(() =>
            {
                Volatile.Write(ref s_refreshQueued, 0);
                try
                {
                    RefreshAttachedGrids(root);
                }
                catch (Exception e)
                {
                    Fault(e);
                }
            });
        }

        // The game's own method, on each attached grid: it takes the
        // prediction of its root, the game converting its physics and its
        // constraints accordingly.
        public static void RefreshAttachedGrids(MyCubeGrid root)
        {
            var hierarchy = MyGridPhysicalHierarchy.Static;
            if (hierarchy == null || root == null || root.Closed)
                return;

            var seen = new HashSet<long> { root.EntityId };
            var pending = new Queue<KeyValuePair<MyCubeGrid, int>>();
            pending.Enqueue(new KeyValuePair<MyCubeGrid, int>(root, 0));
            var children = new List<MyCubeGrid>();
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (current.Value >= MaxDepth)
                    continue;
                hierarchy.ApplyOnChildren(current.Key, child =>
                {
                    if (!seen.Add(child.EntityId))
                        return;
                    children.Add(child);
                    pending.Enqueue(new KeyValuePair<MyCubeGrid, int>(child, current.Value + 1));
                });
            }

            foreach (var child in children)
            {
                if (!child.Closed && child.InScene && child.Physics != null)
                    child.UpdatePredictionFlag();
            }
        }

        // Turned on in a game: constraints the game disabled while the switch
        // was off are enabled, as if the switch had been on from the start.
        // Turned off: the game's own method disables them again where it
        // would have.
        public static void ResyncConstraints(bool clientConstraints)
        {
            var properties = new Dictionary<Type, KeyValuePair<PropertyInfo, PropertyInfo>>();
            foreach (var entity in MyEntities.GetEntities())
            {
                var grid = entity as MyCubeGrid;
                if (grid == null || grid.Closed || grid.Physics == null)
                    continue;

                try
                {
                    if (!clientConstraints)
                    {
                        grid.Physics.UpdateConstraintsForceDisable();
                        continue;
                    }

                    var constraints = Names.Constraints.GetValue(grid.Physics, null) as IEnumerable;
                    if (constraints == null)
                        continue;
                    var list = new List<object>();
                    foreach (var constraint in constraints)
                        list.Add(constraint);
                    foreach (var constraint in list)
                    {
                        if (constraint == null)
                            continue;
                        // HkConstraint.ForceDisabled and InWorld, the two
                        // properties the game's own UpdateConstraintForceDisable
                        // uses: like the game, only a constraint in the
                        // physics world is touched.
                        KeyValuePair<PropertyInfo, PropertyInfo> pair;
                        var type = constraint.GetType();
                        if (!properties.TryGetValue(type, out pair))
                        {
                            pair = new KeyValuePair<PropertyInfo, PropertyInfo>(type.GetProperty("ForceDisabled"), type.GetProperty("InWorld"));
                            properties[type] = pair;
                        }
                        var forceDisabled = pair.Key;
                        var inWorld = pair.Value;
                        if (forceDisabled == null || !forceDisabled.CanWrite || forceDisabled.PropertyType != typeof(bool)
                            || inWorld == null || inWorld.PropertyType != typeof(bool))
                            continue;
                        if (!(bool)inWorld.GetValue(constraint, null))
                            continue;
                        if ((bool)forceDisabled.GetValue(constraint, null))
                            forceDisabled.SetValue(constraint, false, null);
                    }
                }
                catch (Exception e)
                {
                    SirCorrectifsPulsarPlugin.Log("constraints of grid " + grid.EntityId + " not updated: " + e.Message);
                }
            }
        }

        // Every grid gets the prediction the game decides with the current
        // switches: roots first, then the grids attached to them.
        public static void RecomputeAll()
        {
            var hierarchy = MyGridPhysicalHierarchy.Static;
            if (hierarchy == null)
                return;

            var roots = new List<MyCubeGrid>();
            var attached = new List<MyCubeGrid>();
            foreach (var entity in MyEntities.GetEntities())
            {
                var grid = entity as MyCubeGrid;
                if (grid == null || grid.Closed || !grid.InScene || grid.Physics == null)
                    continue;
                if (hierarchy.GetRoot(grid) == grid)
                    roots.Add(grid);
                else
                    attached.Add(grid);
            }

            foreach (var grid in roots)
                Recompute(grid);
            foreach (var grid in attached)
                Recompute(grid);
        }

        private static void Recompute(MyCubeGrid grid)
        {
            try
            {
                grid.UpdatePredictionFlag();
            }
            catch (Exception e)
            {
                SirCorrectifsPulsarPlugin.Log("prediction of grid " + grid.EntityId + " not recomputed: " + e.Message);
            }
        }

        private static void Fault(Exception e)
        {
            // At once, on every thread: the hook answers like the game again.
            Set(false, false);
            var stop = Stop;
            if (stop != null)
                stop.Stop("fault while the game decided a prediction: " + e, Texts.StopFault);
        }
    }
}
