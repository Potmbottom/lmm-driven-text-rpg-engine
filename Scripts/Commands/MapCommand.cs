using Godot;
using RPG.Core;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RPG.Tools
{
    public class MapCommand
    {
        private readonly MapGenerator _mapGenerator;

        public MapCommand(MapGenerator mapGenerator)
        {
            _mapGenerator = mapGenerator;
        }

        public async Task ExecuteAsync()
        {
            if (_mapGenerator == null)
            {
                GD.PrintErr("MapCommand: MapGenerator reference is missing!");
                return;
            }

            GD.Print("🗺️ Generating manual map...");

            var world = StateManager.Instance.CurrentWorld;
            
            // 1. Вычисляем центр
            var center = GetCenter(world);
            
            // 2. Определяем границы (радиус обзора 8 клеток)
            int radius = 8;
            var bounds = new Rect2I(center.X - radius, center.Y - radius, radius * 2 + 1, radius * 2 + 1);

            // 3. Классифицируем клетки
            var (freeCells, occupiedCells) = ClassifyCells(world, bounds);

            // 4. Генерируем изображение
            var image = await _mapGenerator.GenerateMapImage(freeCells, occupiedCells, bounds);

            if (image != null)
            {
                string path = "res://manual_debug_map.png";
                image.SavePng(path);
                GD.Print($"✅ Map saved to: {path}");
            }
            else
            {
                GD.PrintErr("❌ Failed to generate map image.");
            }
        }

        private GridCoordinate GetCenter(WorldState world)
        {
            if (world.Locations.Count == 0) return new GridCoordinate(0, 0, 0);

            int lastActiveId = 0;
            // Ищем последнее упоминание локации в истории
            if (world.History.Texts.Count > 0)
            {
                var lastEntry = world.History.Texts.LastOrDefault(t => t.Locations != null && t.Locations.Count > 0);
                if (lastEntry != null) lastActiveId = lastEntry.Locations.Last();
            }

            // Если нашли ID, ищем саму локацию
            var loc = world.Locations.FirstOrDefault(l => l.Id == lastActiveId);
            
            // Если локация найдена и у неё есть клетки - считаем среднее арифметическое
            if (loc != null && loc.CellIndices.Count > 0)
            {
                long sumX = 0, sumY = 0;
                int count = 0;
                foreach (var idx in loc.CellIndices)
                {
                    try
                    {
                        var c = GridCoordinate.Parse(idx);
                        sumX += c.X;
                        sumY += c.Y;
                        count++;
                    }
                    catch { /* ignore bad coords */ }
                }
                if (count > 0)
                {
                    return new GridCoordinate((int)Math.Round((double)sumX / count), (int)Math.Round((double)sumY / count), 0);
                }
            }

            return new GridCoordinate(0, 0, 0);
        }

        private (List<string> free, List<string> occupied) ClassifyCells(WorldState world, Rect2I bounds)
        {
            var free = new List<string>();
            var occupied = new List<string>();

            // Собираем все занятые клетки мира в HashSet для быстрого поиска
            var allOccupiedInWorld = world.Locations
                .SelectMany(l => l.CellIndices)
                .ToHashSet();

            for (int y = bounds.Position.Y; y < bounds.Position.Y + bounds.Size.Y; y++)
            {
                for (int x = bounds.Position.X; x < bounds.Position.X + bounds.Size.X; x++)
                {
                    string index = $"{x}:{y}:0"; // Z всегда 0 для карты
                    
                    if (allOccupiedInWorld.Contains(index))
                    {
                        occupied.Add(index);
                    }
                    else
                    {
                        free.Add(index);
                    }
                }
            }

            return (free, occupied);
        }
    }
}