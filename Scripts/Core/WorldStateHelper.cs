using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RPG.Models;

namespace RPG.Core.Helpers
{
    public static class WorldStateHelper
    {
        /// <summary>
        /// Находит ближайшую к указанному индексу ячейки локацию.
        /// Учитывает только локации на том же уровне Z.
        /// </summary>
        /// <param name="targetCellIndex">Индекс ячейки в формате "X:Y:Z"</param>
        /// <param name="allLocations">Список всех локаций мира</param>
        /// <returns>Ближайшая LocationData или null, если локаций нет</returns>
        public static LocationData FindNearestLocation(string targetCellIndex, IEnumerable<LocationData> allLocations)
        {
            if (string.IsNullOrEmpty(targetCellIndex) || allLocations == null || !allLocations.Any())
                return null;

            var targetCoord = GridCoordinate.Parse(targetCellIndex);
            LocationData nearestLocation = null;
            double minDistance = double.MaxValue;

            foreach (var loc in allLocations)
            {
                var locIndices = loc.GetCellIndices();
                if (!locIndices.Any()) continue;

                foreach (var locCellStr in locIndices)
                {
                    var locCoord = GridCoordinate.Parse(locCellStr);

                    // Пропускаем, если локация находится на другом уровне Z
                    if (locCoord.Z != targetCoord.Z) continue;

                    // Вычисляем Евклидово расстояние (по X и Y)
                    double dx = targetCoord.X - locCoord.X;
                    double dy = targetCoord.Y - locCoord.Y;
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    // Если расстояние 0, значит ячейка прямо внутри этой локации
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

        public static string FormatLocationData(IEnumerable<LocationData> locations, List<ObjectData> allWorldObjects, HashSet<int> activeLocations)
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

        /// <summary>
        /// Форматирует список групп в текст.
        /// </summary>
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

        /// <summary>
        /// Форматирует конкретные объекты (и их потомков).
        /// </summary>
        public static string FormatObjectData(IEnumerable<ObjectData> objects, List<ObjectData> allWorldObjects)
        {
            var sb = new StringBuilder();
            var childrenLookup = BuildChildrenLookup(allWorldObjects);

            // Чтобы не дублировать детей, если они переданы в списке вместе с родителями,
            // можно сначала отфильтровать, но здесь мы просто выводим то, что передали.
            foreach (var obj in objects)
            {
                AppendObjectRecursive(sb, obj, childrenLookup, indentLevel: 0);
            }

            return sb.ToString();
        }

        // --- Private Helper Methods ---

        private static void AppendGroupData(StringBuilder sb, GroupData group, List<ObjectData> allObjects,
            ILookup<int, ObjectData> childrenLookup, int indentLevel)
        {
            string indent = new string('\t', indentLevel);
            sb.AppendLine($"{indent}Group:");
            if (!string.IsNullOrEmpty(group.Description))
            {
                sb.AppendLine($"{indent}\tDescription: {group.Description}");
            }

            // 1. Cells Indexes
            sb.AppendLine($"{indent}\tcells_indexes: [{string.Join(", ", group.CellIndices)}]");

            // 2. Objects in this group
            sb.AppendLine($"{indent}\tobjects:");

            // Находим корневые объекты, которые находятся в ячейках этой группы
            var groupCellSet = group.CellIndices.ToHashSet();

            var rootObjectsInGroup = allObjects
                .Where(o => o.ParentObjectId == null && // Только корневые
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
            string indent = new string('\t', indentLevel);

            // Формируем заголовок объекта
            string cellInfo = (currentObj.CellIndices != null && currentObj.CellIndices.Any())
                ? $" [Cells: {string.Join(",", currentObj.CellIndices)}]"
                : "";

            sb.AppendLine($"{indent}Object (ID: {currentObj.Id}){cellInfo}");

            // Description / History
            // Берем последнее событие из истории как описание или выводим всю историю
            if (currentObj.History != null && currentObj.History.Count > 0)
            {
                // Вариант 1: Выводим всё одной строкой
                sb.AppendLine($"{indent}\tdescription: {string.Join("; ", currentObj.History)}");

                // Вариант 2: Выводим последнюю запись как текущее состояние
                //sb.AppendLine($"{indent}\tdescription: {currentObj.History.Last()}");
            }
            else
            {
                sb.AppendLine($"{indent}\tdescription: (no data)");
            }

            // Рекурсивно выводим детей
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

        /// <summary>
        /// Метод 1: Собирает иерархию для конкретного объекта.
        /// Возвращает плоский список, где [0] - это запрашиваемый объект, а далее идут все его потомки (рекурсивно).
        /// </summary>
        public static List<ObjectData> GetObjectHierarchy(int rootObjectId, List<ObjectData> allObjects)
        {
            var result = new List<ObjectData>();

            // Находим целевой объект
            var rootObj = allObjects.FirstOrDefault(o => o.Id == rootObjectId);
            if (rootObj == null) return result;

            // Оптимизация: Создаем lookup таблицу (ParentID -> Список детей) для всего мира один раз
            // Это намного быстрее, чем делать .Where внутри рекурсии
            var childrenLookup = allObjects
                .Where(o => o.ParentObjectId.HasValue)
                .ToLookup(o => o.ParentObjectId.Value);

            var visited = new HashSet<int>();

            // Запускаем рекурсивный сбор
            CollectDescendantsRecursive(rootObj, childrenLookup, result, visited);

            return result;
        }

        /// <summary>
        /// Приватный рекурсивный метод обхода
        /// </summary>
        private static void CollectDescendantsRecursive(
            ObjectData current,
            ILookup<int, ObjectData> childrenLookup,
            List<ObjectData> accumulatedList,
            HashSet<int> visited)
        {
            if (visited.Contains(current.Id)) return; // Защита от циклов (A -> B -> A)

            visited.Add(current.Id);
            accumulatedList.Add(current);

            // Если у текущего объекта есть дети
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
            public List<LocationData> Locations { get; set; } // Список новых захваченных локаций
            public string CenterCellIndex { get; set; } // Координата центра квадрата
            public int SideLength { get; set; } // Длина стороны итогового квадрата
        }

        public static ExtendedAreaResult GetExtendedArea(IEnumerable<LocationData> inputLocations, int minSize,
            List<LocationData> allWorldLocations)
        {
            if (inputLocations == null || !inputLocations.Any()) return null;

            // Вспомогательная структура для работы с границами
            var rects = new List<(int minX, int maxX, int minY, int maxY, int Z)>();

            // 1. Находим границы и центры для каждой входной локации
            foreach (var loc in inputLocations)
            {
                var coords = loc.GetCellIndices().Select(GridCoordinate.Parse).ToList();
                if (!coords.Any()) continue;

                int minX = coords.Min(c => c.X);
                int maxX = coords.Max(c => c.X);
                int minY = coords.Min(c => c.Y);
                int maxY = coords.Max(c => c.Y);
                int Z = coords[0].Z;

                // Находим центр текущей локации
                float centerX = (minX + maxX) / 2f;
                float centerY = (minY + maxY) / 2f;

                // 2. Описываем квадрат с минимальным размером N вокруг центра локации
                int width = maxX - minX;
                int height = maxY - minY;

                int currentRectSide = Math.Max(Math.Max(width, height), minSize);

                // Вычисляем новые границы этого конкретного квадрата
                int rMinX = (int)Math.Floor(centerX - currentRectSide / 2f);
                int rMaxX = rMinX + currentRectSide;
                int rMinY = (int)Math.Floor(centerY - currentRectSide / 2f);
                int rMaxY = rMinY + currentRectSide;

                rects.Add((rMinX, rMaxX, rMinY, rMaxY, Z));
            }

            if (!rects.Any()) return null;

            // 3. Описываем общий квадрат, в который впишутся все созданные выше квадраты
            int globalMinX = rects.Min(r => r.minX);
            int globalMaxX = rects.Max(r => r.maxX);
            int globalMinY = rects.Min(r => r.minY);
            int globalMaxY = rects.Max(r => r.maxY);
            int commonZ = rects[0].Z;

            int globalWidth = globalMaxX - globalMinX;
            int globalHeight = globalMaxY - globalMinY;

            // Итоговая сторона квадрата — это максимум из ширины и высоты общей области
            int finalSide = Math.Max(globalWidth, globalHeight);

            // Вычисляем центр итогового квадрата
            int finalCenterX = globalMinX + globalWidth / 2;
            int finalCenterY = globalMinY + globalHeight / 2;

            // Границы итогового сканирующего квадрата
            int scanMinX = finalCenterX - finalSide / 2;
            int scanMaxX = scanMinX + finalSide;
            int scanMinY = finalCenterY - finalSide / 2;
            int scanMaxY = scanMinY + finalSide;

            // 4. Составляем список локаций, попавших в этот квадрат
            var inputIds = inputLocations.Select(l => l.Id).ToHashSet();
            var capturedLocations = new List<LocationData>();

            foreach (var loc in allWorldLocations)
            {
                // Исключаем те, что были переданы изначально
                if (inputIds.Contains(loc.Id)) continue;

                // Проверяем, есть ли хоть одна ячейка локации в границах квадрата
                bool isIntersecting = loc.GetCellIndices().Any(idx =>
                {
                    var c = GridCoordinate.Parse(idx);
                    return c.X >= scanMinX && c.X <= scanMaxX &&
                           c.Y >= scanMinY && c.Y <= scanMaxY &&
                           c.Z == commonZ;
                });

                if (isIntersecting)
                {
                    capturedLocations.Add(loc);
                }
            }

            return new ExtendedAreaResult
            {
                Locations = capturedLocations,
                CenterCellIndex = new GridCoordinate(finalCenterX, finalCenterY, commonZ).ToString(),
                SideLength = finalSide
            };
        }

        public static List<string> GetCellIndices(this LocationData location)
        {
            return location.Groups.SelectMany(data => data.CellIndices).ToList();
        }
        
        public static List<string> GetCellIndices(this List<GroupData> groups)
        {
            return groups.SelectMany(data => data.CellIndices).ToList();
        }
    }
}