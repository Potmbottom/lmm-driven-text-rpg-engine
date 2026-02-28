using Godot;
using RPG.Core.Helpers;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RPG.Core;

namespace RPG.Tools
{
    public partial class MapGenerator : Node
    {
        [ExportCategory("Settings")]
        [Export] public int CellSize = 128;
        [Export] public int BaseFontSize = 16; 
        [Export] public SubViewport TargetViewport;
        [Export] public Control ViewportRoot;

        [ExportCategory("Output")]
        [Export] public bool AutoSave = true;
        [Export] public string SaveDirectory = "res://Database/maps";

        private readonly Color[] _locationColors = new Color[]
        {
            new Color("e34234"), // Vermilion
            new Color("2e8b57"), // Sea Green
            new Color("ffbf00"), // Amber
            new Color("ff00ff"), // Magenta
        };

        private readonly Dictionary<int, Color> _colorCache = new Dictionary<int, Color>();
        
        public async Task<Image> GenerateMapAround(string centerIndex, int sideLength, 
            IEnumerable<LocationData> locationsToRender, 
            HashSet<int> activeIds = null)
        {
            if (TargetViewport == null || ViewportRoot == null) 
            {
                GD.PrintErr("MapGenerator: TargetViewport or ViewportRoot is missing.");
                return null;
            }
            
            foreach (var child in ViewportRoot.GetChildren()) child.QueueFree();
            var cellToLocationId = BuildLocationMap(locationsToRender);
            var centerCoord = GridCoordinate.ParseVector(centerIndex);
            var (bounds, gridMap) = GenerateGridSquare(centerCoord, sideLength);
            var calculatedFontSize = Mathf.Max(8, (int)(CellSize * 0.4f));
            var thickBorder = Mathf.Max(1, CellSize / 16);
            var thinBorder = 1;
            var newSize = new Vector2I(bounds.Size.X * CellSize, bounds.Size.Y * CellSize);
            TargetViewport.Size = newSize;
            TargetViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

            var drawBounds = new Rect2I(bounds.Position, bounds.Size);
            int GetLocId(string idx) => cellToLocationId.GetValueOrDefault(idx, -1);
            bool IsOccupied(string idx) => cellToLocationId.ContainsKey(idx);
            bool IsActive(int id) => activeIds == null || activeIds.Contains(id);

            DrawMap(ViewportRoot, drawBounds, gridMap, 
                GetLocId, IsOccupied, IsActive, 
                calculatedFontSize, thickBorder, thinBorder);
            
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var texture = TargetViewport.GetTexture();
            if (texture == null) return null;

            var img = texture.GetImage();

            if (AutoSave && img != null && !img.IsEmpty())
            {
                SaveMapToDisk(img);
            }

            return img;
        }

        public async Task<Image> GenerateMap(IEnumerable<LocationData> activeLocations,
            IEnumerable<LocationData> allWorldLocations)
        {
            return await GenerateMap(activeLocations, allWorldLocations, Array.Empty<string>());
        }

        public async Task<Image> GenerateMap(IEnumerable<LocationData> activeLocations, 
            IEnumerable<LocationData> allWorldLocations, 
            IEnumerable<string> additionalCellIndices, int minSize = 12)
        {
            allWorldLocations ??= new List<LocationData>();

            var areaResult = WorldStateHelper.GetExtendedArea(activeLocations, additionalCellIndices, allWorldLocations.ToList());
            if (areaResult == null) return null;

            var combinedLocations = activeLocations.Concat(areaResult.Locations).ToList();
            var activeIds = activeLocations.Select(l => l.Id).ToHashSet();

            return await GenerateMapAround(
                areaResult.CenterCellIndex, 
                areaResult.SideLength < minSize ? minSize : areaResult.SideLength, 
                combinedLocations, 
                activeIds
            );
        }

        private Dictionary<string, int> BuildLocationMap(IEnumerable<LocationData> locations)
        {
            var map = new Dictionary<string, int>();
            if (locations == null) return map;

            foreach (var loc in locations)
            {
                foreach (var rawIndex in loc.GetCellIndices())
                {
                    var vec = GridCoordinate.ParseVector(rawIndex);
                    map[$"{vec.X}:{vec.Y}"] = loc.Id;
                }
            }
            return map;
        }

        private (Rect2I, Dictionary<Vector2I, string>) GenerateGridSquare(Vector2I center, int sideLength)
        {
            var map = new Dictionary<Vector2I, string>();
            var offset = sideLength / 2;
            var minX = center.X - offset;
            var maxX = minX + sideLength - 1;
            var minY = center.Y - offset;
            var maxY = minY + sideLength - 1;

            for (var x = minX; x <= maxX; x++)
            {
                for (var y = minY; y <= maxY; y++)
                {
                    map[new Vector2I(x, y)] = $"{x}:{y}";
                }
            }

            var bounds = new Rect2I(minX, minY, sideLength, sideLength);
            return (bounds, map);
        }

        public void SaveMapToDisk(Image img, string customName = null)
        {
            if (img == null) return;

            if (!DirAccess.DirExistsAbsolute(SaveDirectory))
            {
                DirAccess.MakeDirRecursiveAbsolute(SaveDirectory);
            }

            var fileName = string.IsNullOrEmpty(customName) 
                ? $"map_{Time.GetDatetimeStringFromSystem().Replace(":", "-").Replace(" ", "_")}.png"
                : (customName.EndsWith(".png") ? customName : $"{customName}.png");

            var fullPath = SaveDirectory.PathJoin(fileName);
            var err = img.SavePng(fullPath);
            
            if (err == Error.Ok)
                GD.Print($"✅ Map saved: {fullPath} ({img.GetWidth()}x{img.GetHeight()})");
            else
                GD.PrintErr($"❌ Failed to save map to {fullPath}. Error: {err}");
        }

        private void DrawMap(Control root, Rect2I logicBounds, Dictionary<Vector2I, string> gridMap, 
                             Func<string, int> getLocationId, Func<string, bool> isOccupied,
                             Func<int, bool> isLocationActive,
                             int fontSize, int thickBorder, int thinBorder)
        {
            var bg = new ColorRect { Color = Colors.Black };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(bg);

            var logicMinX = logicBounds.Position.X;
            var logicMaxX = logicBounds.End.X;
            var logicMinY = logicBounds.Position.Y;
            var logicMaxY = logicBounds.End.Y;
            var worldTopY = logicMaxY - 1;

            for (var x = logicMinX; x < logicMaxX; x++)
            {
                for (var y = logicMinY; y < logicMaxY; y++)
                {
                    var gridPos = new Vector2I(x, y);

                    var relX = x - logicMinX;
                    var relY = worldTopY - y; 

                    float screenX = relX * CellSize;
                    float screenY = relY * CellSize;

                    var indexStr = gridMap.GetValueOrDefault(gridPos);
                    
                    DrawCell(root, new Vector2(screenX, screenY), gridPos, indexStr, gridMap, 
                        getLocationId, isOccupied, isLocationActive, 
                        fontSize, thickBorder, thinBorder);
                }
            }
        }

        private void DrawCell(Control root, Vector2 screenPos, Vector2I gridPos, string currentIndexStr, 
                            Dictionary<Vector2I, string> gridMap, 
                            Func<string, int> getLocationId, Func<string, bool> isOccupied,
                            Func<int, bool> isLocationActive,
                            int fontSize, int thickBorder, int thinBorder)
        {
            var cellContainer = new Control();
            cellContainer.Position = screenPos;
            cellContainer.Size = new Vector2(CellSize, CellSize);
            root.AddChild(cellContainer);

            var currentOccupied = currentIndexStr != null && isOccupied(currentIndexStr);
            var currentLocationId = currentOccupied ? getLocationId(currentIndexStr) : -1;
            
            var baseColor = currentOccupied ? GetLocationColor(currentLocationId) : Colors.White;
            var isActive = isLocationActive(currentLocationId);
            
            if (currentOccupied && !isActive)
            {
                cellContainer.Modulate = new Color(1, 1, 1, 0.4f); 
            }
            
            if (fontSize > 4)
            {
                var label = new Label
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    ZIndex = 3,
                    Text = currentOccupied 
                        ? (isActive ? $"{gridPos.X}:{gridPos.Y}" : $"{currentLocationId}") 
                        : $"{gridPos.X}:{gridPos.Y}"
                };
                
                label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                label.AddThemeColorOverride("font_color", Colors.White);
                label.AddThemeFontSizeOverride("font_size", fontSize);
                label.ClipText = true; 
                
                cellContainer.AddChild(label);
            }
            CheckBorder(cellContainer, Side.Top, new Vector2I(gridPos.X, gridPos.Y + 1), currentOccupied, currentLocationId, baseColor, gridMap, getLocationId, isOccupied, thickBorder, thinBorder);
            CheckBorder(cellContainer, Side.Bottom, new Vector2I(gridPos.X, gridPos.Y - 1), currentOccupied, currentLocationId, baseColor, gridMap, getLocationId, isOccupied, thickBorder, thinBorder);
            CheckBorder(cellContainer, Side.Left, new Vector2I(gridPos.X - 1, gridPos.Y), currentOccupied, currentLocationId, baseColor, gridMap, getLocationId, isOccupied, thickBorder, thinBorder);
            CheckBorder(cellContainer, Side.Right, new Vector2I(gridPos.X + 1, gridPos.Y), currentOccupied, currentLocationId, baseColor, gridMap, getLocationId, isOccupied, thickBorder, thinBorder);
        }

        private enum Side { Top, Bottom, Left, Right }

        private void CheckBorder(Control parent, Side side, Vector2I neighborPos, 
                               bool currentOccupied, int currentLocId, Color currentColor,
                               Dictionary<Vector2I, string> gridMap, 
                               Func<string, int> getLocationId, Func<string, bool> isOccupied,
                               int thick, int thin)
        {
            var neighborIndexStr = gridMap.GetValueOrDefault(neighborPos);
            
            var neighborOccupied = false;
            var neighborLocId = -1;

            if (neighborIndexStr != null)
            {
                neighborOccupied = isOccupied(neighborIndexStr);
                neighborLocId = neighborOccupied ? getLocationId(neighborIndexStr) : -1;
            }
            
            if (currentOccupied)
            {
                if (!neighborOccupied || neighborLocId != currentLocId)
                {
                    CreateBorderLine(parent, side, currentColor, thick, 2); 
                }
            }
            else 
            {
                if (!neighborOccupied)
                {
                    CreateBorderLine(parent, side, Colors.White, thin, 1); 
                }
            }
        }

        private void CreateBorderLine(Control parent, Side side, Color color, int thickness, int zIndex)
        {
            var lineNode = new ColorRect
            {
                Color = color,
                ZIndex = zIndex
            };
            parent.AddChild(lineNode);
            
            switch (side)
            {
                case Side.Top:
                    lineNode.Position = Vector2.Zero;
                    lineNode.Size = new Vector2(CellSize, thickness);
                    break;
                case Side.Bottom:
                    lineNode.Position = new Vector2(0, CellSize - thickness);
                    lineNode.Size = new Vector2(CellSize, thickness);
                    break;
                case Side.Left:
                    lineNode.Position = Vector2.Zero;
                    lineNode.Size = new Vector2(thickness, CellSize);
                    break;
                case Side.Right:
                    lineNode.Position = new Vector2(CellSize - thickness, 0);
                    lineNode.Size = new Vector2(thickness, CellSize);
                    break;
            }
        }

        private Color GetLocationColor(int id)
        {
            if (id < 0) return Colors.Gray;
            if (!_colorCache.ContainsKey(id))
            {
                _colorCache[id] = _locationColors[Math.Abs(id) % _locationColors.Length];
            }
            return _colorCache[id];
        }
    }
}