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
            var image = await _mapGenerator.GenerateMap(StateManager.Instance.CurrentWorld.Locations, StateManager.Instance.CurrentWorld.Locations);
            if (image != null)
            {
                var path = "res://manual_debug_map.png";
                image.SavePng(path);
                GD.Print($"✅ Map saved to: {path}");
            }
            else
            {
                GD.PrintErr("❌ Failed to generate map image.");
            }
        }
    }
}