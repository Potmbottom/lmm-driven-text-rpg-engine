using System.Collections.Generic;
using System.Linq;
using RPG.Models;

namespace RPG.Core.Helpers
{
    public static class WorldStateHelper
    {
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
        /// Метод 2: Собирает иерархии для всех корневых объектов в указанной локации.
        /// Возвращает список списков, где каждый внутренний список представляет собой дерево одного корневого объекта.
        /// </summary>
        public static List<List<ObjectData>> GetLocationHierarchies(int locationId, List<LocationData> allLocations, List<ObjectData> allObjects)
        {
            var result = new List<List<ObjectData>>();

            // 1. Находим локацию и её ячейки
            var location = allLocations.FirstOrDefault(l => l.Id == locationId);
            if (location == null || location.Groups == null) return result;

            // Используем HashSet для быстрого поиска индексов
            var locCellIndices = location.CellIndices.ToHashSet();

            // 2. Подготавливаем lookup детей для всего списка объектов (оптимизация)
            var childrenLookup = allObjects
                .Where(o => o.ParentObjectId.HasValue)
                .ToLookup(o => o.ParentObjectId.Value);

            // 3. Находим "Корневые" объекты:
            // - ParentObjectId == null (не лежат в другом объекте)
            // - Ссылаются на ячейку, которая принадлежит этой локации
            var rootObjects = allObjects
                .Where(o => o.ParentObjectId == null && 
                            o.CellIndices != null && 
                            o.CellIndices.Any(idx => locCellIndices.Contains(idx)))
                .ToList();

            var visitedGlobal = new HashSet<int>();

            // 4. Для каждого корневого объекта собираем его дерево
            foreach (var root in rootObjects)
            {
                // Защита: если объект каким-то образом уже был посещен (странная структура данных), пропускаем
                if (visitedGlobal.Contains(root.Id)) continue;

                var hierarchy = new List<ObjectData>();
                
                // Используем локальный visited для текущей ветки, но можно использовать и глобальный,
                // если мы уверены, что один объект не может быть в двух иерархиях одновременно.
                CollectDescendantsRecursive(root, childrenLookup, hierarchy, visitedGlobal);

                if (hierarchy.Count > 0)
                {
                    result.Add(hierarchy);
                }
            }

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
    }
}