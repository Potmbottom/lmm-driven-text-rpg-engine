using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using RPG.Core;
using RPG.Core.Helpers;
using RPG.Models;
using RPG.Models.Simulation;

namespace RPG.Tools
{
    public static class SimulationHelper
    {
        public static string BuildContext(
            IEnumerable<LocationData> locations,
            HashSet<int> activeLocations,
            List<ObjectData> objects,
            QueryMetaData metaData,
            List<string> recentHistory,
            List<string> log)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Query Meta Data:");
            sb.AppendLine(JsonUtils.Serialize(metaData));
            sb.AppendLine();
            var historyContext = (recentHistory != null && recentHistory.Count > 0)
                ? string.Join("\n", recentHistory.Select(h => $"- {h}"))
                : "(No recent history)";
            sb.AppendLine(historyContext);
            sb.AppendLine();
            sb.AppendLine("World Context:");
            sb.Append(WorldStateHelper.FormatLocationData(locations, objects, activeLocations));
            sb.AppendLine();
            sb.AppendLine($"Request Log: {JsonUtils.Serialize(log)}");
            return sb.ToString();
        }

        public static (List<int>,string) ApplyActions(List<SimulationAction> actions, List<ObjectData> objects,
            List<LocationData> locations)
        {
            if (actions == null) return ([],"");

            var result = new StringBuilder();
            var objectsId = new List<int>();
            foreach (var act in actions)
            {
                try
                {
                    switch (act.Type)
                    {
                        case "move_cell":
                            var mc = JsonSerializer.Deserialize<ActionMoveToCell>(act.ActionData.GetRawText());
                            var objC = objects.FirstOrDefault(o => o.Id == mc.ObjectId);
                            if (objC != null)
                            {
                                objC.CellIndices = new List<string> { mc.SetId };
                                objC.ParentObjectId = null;
                            }
                            result.Append($"Type {act.Type}. ObjectId: {mc.ObjectId}");
                            objectsId.Add(mc.ObjectId);
                            break;
                        case "move_object":
                            var mo = JsonSerializer.Deserialize<ActionMoveToObject>(act.ActionData.GetRawText());
                            var objO = objects.FirstOrDefault(o => o.Id == mo.ObjectId);
                            if (objO != null)
                            {
                                objO.ParentObjectId = mo.SetId;
                                objO.CellIndices = null;
                            }
                            result.Append($"Type {act.Type}. ObjectId: {mo.ObjectId}");
                            objectsId.Add(mo.SetId);
                            break;
                        case "expand_history":
                            var he = JsonSerializer.Deserialize<ActionExpandHistory>(act.ActionData.GetRawText());
                            var objH = objects.FirstOrDefault(o => o.Id == he.ObjectId);
                            if (objH != null) objH.History.Add(he.NewText);
                            result.Append($"Type {act.Type}. ObjectId: {he.ObjectId}\nText: {he.NewText}");
                            objectsId.Add(he.ObjectId);
                            break;
                        case "update_group":
                            var gu = JsonSerializer.Deserialize<ActionGroupUpdate>(act.ActionData.GetRawText());
                            foreach (var loc in locations)
                            {
                                foreach (var grp in loc.Groups)
                                {
                                    if (grp.CellIndices.Intersect(gu.CellsId).Any())
                                        grp.Description = gu.NewText;
                                }
                            }
                            break;
                        case "update_location":
                            var lo = JsonSerializer.Deserialize<ActionLocationUpdate>(act.ActionData.GetRawText());
                            var location = locations.FirstOrDefault(l => l.Id == lo.LocationId);
                            location.Description = lo.NewText;
                            break;
                        case "update_key":
                            var uk = JsonSerializer.Deserialize<ActionUpdateKey>(act.ActionData.GetRawText());
                            var targetObj = objects.FirstOrDefault(o => o.Id == uk.TargetId);
                            if (targetObj != null)
                            {
                                UpdateKeysList(targetObj.Keys, uk);
                                result.Append($"Key Update (Obj {uk.TargetId}): +{uk.NewKey}");
                                objectsId.Add(uk.TargetId);
                            }
                            else 
                            {
                                var targetLoc = locations.FirstOrDefault(l => l.Id == uk.TargetId);
                                if (targetLoc != null)
                                {
                                    UpdateKeysList(targetLoc.Keys, uk);
                                    result.Append($"Key Update (Loc {uk.TargetId}): +{uk.NewKey}");
                                }
                            }
                            break;
                        case "emit":
                            break;
                    }
                }
                catch (Exception e)
                {
                    GD.PrintErr($"Action application failed: {e.Message}");
                }
                result.AppendLine();
            }
            return (objectsId, result.ToString());
        }
        
        private static void UpdateKeysList(List<string> keys, ActionUpdateKey data)
        {
            if (!string.IsNullOrEmpty(data.OldKey) && keys.Contains(data.OldKey))
            {
                keys.Remove(data.OldKey);
            }
            
            if (!string.IsNullOrEmpty(data.NewKey) && !keys.Contains(data.NewKey))
            {
                keys.Add(data.NewKey);
            }
        }

        public static (bool IsSuccess, int roll) PerformSkillCheck(CheckBreakData data)
        {
            var roll = GD.RandRange(1, 100);
            var success = roll > data.Payload; 
            return (success, roll);
        }
    }
}