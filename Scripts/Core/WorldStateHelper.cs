using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RPG.Models;

namespace RPG.Core.Helpers
{
    public static class WorldStateHelper
    {
        public static LocationData FindNearestLocation(string targetCellIndex, IEnumerable<LocationData> allLocations)
        {
            if (string.IsNullOrEmpty(targetCellIndex) || allLocations == null || !allLocations.Any())
                return null;

            var targetCoord = GridCoordinate.Parse(targetCellIndex);
            LocationData nearestLocation = null;
            var minDistance = double.MaxValue;

            foreach (var loc in allLocations)
            {
                var locIndices = loc.GetCellIndices();
                if (!locIndices.Any()) continue;

                foreach (var locCellStr in locIndices)
                {
                    var locCoord = GridCoordinate.Parse(locCellStr);
                    if (locCoord.Z != targetCoord.Z) continue;
                    double dx = targetCoord.X - locCoord.X;
                    double dy = targetCoord.Y - locCoord.Y;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    if (distance < 0.001) return loc;

                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        nearestLocation = loc;
                    }
                }
            }

            return nearestLocation;
        }

        public static string FormatLocationData(IEnumerable<LocationData> locations, List<ObjectData> allWorldObjects)
        {
            return FormatLocationData(locations, allWorldObjects, locations.Select(data => data.Id).ToHashSet());
        }

        public static string FormatLocationData(IEnumerable<LocationData> locations, List<ObjectData> allWorldObjects,
            HashSet<int> activeLocations)
        {
            var sb = new StringBuilder();
            var childrenLookup = BuildChildrenLookup(allWorldObjects);

            foreach (var loc in locations)
            {
                sb.AppendLine($"Location (ID: {loc.Id}):");
                sb.AppendLine($"\tDescription: {loc.Description}");

                if (loc.Groups != null && activeLocations.Contains(loc.Id))
                {
                    foreach (var group in loc.Groups)
                    {
                        AppendGroupData(sb, group, allWorldObjects, childrenLookup, indentLevel: 1);
                    }
                }

                sb.AppendLine(new string('-', 20));
            }

            return sb.ToString();
        }
        
        public static string FormatGroupData(IEnumerable<GroupData> groups, List<ObjectData> allWorldObjects)
        {
            var sb = new StringBuilder();
            var childrenLookup = BuildChildrenLookup(allWorldObjects);

            foreach (var group in groups)
            {
                AppendGroupData(sb, group, allWorldObjects, childrenLookup, indentLevel: 0);
                sb.AppendLine(new string('-', 20));
            }

            return sb.ToString();
        }
        
        public static string FormatObjectData(IEnumerable<ObjectData> objects, List<ObjectData> allWorldObjects)
        {
            var sb = new StringBuilder();
            var childrenLookup = BuildChildrenLookup(allWorldObjects);
            foreach (var obj in objects)
            {
                AppendObjectRecursive(sb, obj, childrenLookup, indentLevel: 0);
            }

            return sb.ToString();
        }

        private static void AppendGroupData(StringBuilder sb, GroupData group, List<ObjectData> allObjects,
            ILookup<int, ObjectData> childrenLookup, int indentLevel)
        {
            var indent = new string('\t', indentLevel);
            sb.AppendLine($"{indent}Group:");
            if (!string.IsNullOrEmpty(group.Description))
            {
                sb.AppendLine($"{indent}\tDescription: {group.Description}");
            }
            
            sb.AppendLine($"{indent}\tcells_indexes: [{string.Join(", ", group.CellIndices)}]");
            sb.AppendLine($"{indent}\tobjects:");
            
            var groupCellSet = group.CellIndices.ToHashSet();

            var rootObjectsInGroup = allObjects
                .Where(o => o.ParentObjectId == null &&
                            o.CellIndices != null &&
                            o.CellIndices.Any(idx => groupCellSet.Contains(idx)))
                .ToList();

            if (rootObjectsInGroup.Count == 0)
            {
                sb.AppendLine($"{indent}\t\t(empty)");
            }
            else
            {
                foreach (var rootObj in rootObjectsInGroup)
                {
                    AppendObjectRecursive(sb, rootObj, childrenLookup, indentLevel + 2);
                }
            }
        }

        private static void AppendObjectRecursive(StringBuilder sb, ObjectData currentObj,
            ILookup<int, ObjectData> childrenLookup, int indentLevel)
        {
            var indent = new string('\t', indentLevel);
            var cellInfo = (currentObj.CellIndices != null && currentObj.CellIndices.Any())
                ? $" [Cells: {string.Join(",", currentObj.CellIndices)}]"
                : "";

            sb.AppendLine($"{indent}Object (ID: {currentObj.Id}){cellInfo}");
            if (currentObj.History != null && currentObj.History.Count > 0)
            {
                sb.AppendLine($"{indent}\tdescription: {string.Join("; ", currentObj.History)}");
            }
            else
            {
                sb.AppendLine($"{indent}\tdescription: (no data)");
            }
            
            if (childrenLookup.Contains(currentObj.Id))
            {
                foreach (var child in childrenLookup[currentObj.Id])
                {
                    AppendObjectRecursive(sb, child, childrenLookup, indentLevel + 1);
                }
            }
        }

        private static ILookup<int, ObjectData> BuildChildrenLookup(List<ObjectData> allObjects)
        {
            return allObjects
                .Where(o => o.ParentObjectId.HasValue)
                .ToLookup(o => o.ParentObjectId.Value);
        }
        
        public static List<ObjectData> GetObjectHierarchy(int rootObjectId, List<ObjectData> allObjects)
        {
            var result = new List<ObjectData>();
            
            var rootObj = allObjects.FirstOrDefault(o => o.Id == rootObjectId);
            if (rootObj == null) return result;
            var childrenLookup = allObjects
                .Where(o => o.ParentObjectId.HasValue)
                .ToLookup(o => o.ParentObjectId.Value);

            var visited = new HashSet<int>();
            CollectDescendantsRecursive(rootObj, childrenLookup, result, visited);

            return result;
        }
        
        private static void CollectDescendantsRecursive(
            ObjectData current,
            ILookup<int, ObjectData> childrenLookup,
            List<ObjectData> accumulatedList,
            HashSet<int> visited)
        {
            if (visited.Contains(current.Id)) return;

            visited.Add(current.Id);
            accumulatedList.Add(current);
            if (childrenLookup.Contains(current.Id))
            {
                foreach (var child in childrenLookup[current.Id])
                {
                    CollectDescendantsRecursive(child, childrenLookup, accumulatedList, visited);
                }
            }
        }

        public class ExtendedAreaResult
        {
            public List<LocationData> Locations { get; set; }
            public string CenterCellIndex { get; set; }
            public int SideLength { get; set; }
        }
        
        public static ExtendedAreaResult GetExtendedArea(IEnumerable<LocationData> inputLocations,
            List<LocationData> allWorldLocations)
        {
            return GetExtendedArea(inputLocations, null, allWorldLocations);
        }
        
        public static ExtendedAreaResult GetExtendedArea(IEnumerable<LocationData> inputLocations,
            IEnumerable<string> additionalCellIndices,
            List<LocationData> allWorldLocations)
        {
            var hasLocations = inputLocations != null && inputLocations.Any();
            var hasCells = additionalCellIndices != null && additionalCellIndices.Any();
            
            if (!hasLocations && !hasCells) return null;
            
            var minX = int.MaxValue;
            var maxX = int.MinValue;
            var minY = int.MaxValue;
            var maxY = int.MinValue;

            var commonZ = 0;
            var zInitialized = false;
            
            void ProcessCoordinate(GridCoordinate coord)
            {
                if (!zInitialized)
                {
                    commonZ = coord.Z;
                    zInitialized = true;
                }
                
                if (coord.Z != commonZ) return;

                if (coord.X < minX) minX = coord.X;
                if (coord.X > maxX) maxX = coord.X;
                if (coord.Y < minY) minY = coord.Y;
                if (coord.Y > maxY) maxY = coord.Y;
            }
            
            if (hasLocations)
            {
                foreach (var loc in inputLocations)
                {
                    foreach (var cellIndex in loc.GetCellIndices())
                    {
                        ProcessCoordinate(GridCoordinate.Parse(cellIndex));
                    }
                }
            }
            
            if (hasCells)
            {
                foreach (var cellIndex in additionalCellIndices)
                {
                    ProcessCoordinate(GridCoordinate.Parse(cellIndex));
                }
            }
            
            if (!zInitialized) return null;
            minX -= 1;
            maxX += 1;
            minY -= 1;
            maxY += 1;
            
            var requiredWidth = maxX - minX + 1;
            var requiredHeight = maxY - minY + 1;
            
            var sideLength = Math.Max(requiredWidth, requiredHeight);
            var centerX = minX + (sideLength / 2);
            var centerY = minY + (sideLength / 2);
            var scanMinX = centerX - (sideLength / 2);
            var scanMaxX = scanMinX + sideLength;
            var scanMinY = centerY - (sideLength / 2);
            var scanMaxY = scanMinY + sideLength;
            
            if (scanMaxX <= maxX) sideLength++;
            if (scanMaxY <= maxY) sideLength++;
            
            scanMinX = centerX - (sideLength / 2);
            scanMaxX = scanMinX + sideLength;
            scanMinY = centerY - (sideLength / 2);
            scanMaxY = scanMinY + sideLength;
            
            var capturedLocations = new List<LocationData>();
            var inputIds = hasLocations
                ? inputLocations.Select(l => l.Id).ToHashSet()
                : new HashSet<int>();

            foreach (var loc in allWorldLocations)
            {
                if (inputIds.Contains(loc.Id)) continue;
                var isIntersecting = loc.GetCellIndices().Any(idx =>
                {
                    var c = GridCoordinate.Parse(idx);
                    return c.Z == commonZ &&
                           c.X >= scanMinX && c.X < scanMaxX &&
                           c.Y >= scanMinY && c.Y < scanMaxY;
                });

                if (isIntersecting)
                {
                    capturedLocations.Add(loc);
                }
            }

            return new ExtendedAreaResult
            {
                Locations = capturedLocations,
                CenterCellIndex = new GridCoordinate(centerX, centerY, commonZ).ToString(),
                SideLength = sideLength
            };
        }

        public static string GetCurrentWorldTime(List<LocationData> context)
        {
            var state = StateManager.Instance.CurrentWorld;
            var result = "day 1, 06:00:00";
            if (state.History.Texts.Count == 0) return result;
            List<TextEntry> reversed = [..state.History.Texts];
            reversed.Reverse();
            
            foreach (var loc in reversed)
            {
                if(loc.Locations.Count == 0) continue;
                var lastId = loc.Locations.Last();
                return context.First(data => data.Id == lastId).LastUpdateTime;
            }

            return result;
        }

        public static List<string> GetCellIndices(this LocationData location)
        {
            if (location == null) return [];
            return location.Groups.SelectMany(data => data.CellIndices).ToList();
        }

        public static List<string> GetCellIndices(this List<GroupData> groups)
        {
            return groups.SelectMany(data => data.CellIndices).ToList();
        }
    }
}