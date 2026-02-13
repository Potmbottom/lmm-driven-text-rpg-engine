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
            List<LocationData> locations,
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
            string historyContext = (recentHistory != null && recentHistory.Count > 0)
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

        public static void ApplyActions(List<SimulationAction> actions, List<ObjectData> objects,
            List<LocationData> locations)
        {
            if (actions == null) return;

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

                            break;
                        case "move_object":
                            var mo = JsonSerializer.Deserialize<ActionMoveToObject>(act.ActionData.GetRawText());
                            var objO = objects.FirstOrDefault(o => o.Id == mo.ObjectId);
                            if (objO != null)
                            {
                                objO.ParentObjectId = mo.SetId;
                                objO.CellIndices.Clear();
                            }

                            break;
                        case "expand_history":
                            var he = JsonSerializer.Deserialize<ActionExpandHistory>(act.ActionData.GetRawText());
                            var objH = objects.FirstOrDefault(o => o.Id == he.ObjectId);
                            if (objH != null) objH.History.Add(he.NewText);
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
                        case "emit":
                            // Emit is informational
                            break;
                    }
                }
                catch (Exception e)
                {
                    GD.PrintErr($"Action application failed: {e.Message}");
                }
            }
        }

        public static (bool IsSuccess, string Reason) PerformSkillCheck(CheckBreakData data)
        {
            bool success = GD.RandRange(1, 100) > data.Payload; 
            return (success, data.Reason);
        }
    }
}