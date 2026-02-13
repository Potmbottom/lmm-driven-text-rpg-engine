using Godot;
using RPG.Core.Helpers;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RPG.Tools
{
    public partial class MapGenerator : Node
    {
        [ExportCategory("Settings")]
        [Export] public int CellSize = 128;
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

        private Dictionary<int, Color> _colorCache = new Dictionary<int, Color>();

        /// <summary>
        /// Метод 1: Низкоуровневая генерация карты вокруг точки с конкретным набором данных.
        /// </summary>
        /// <param name="centerIndex">Центральная ячейка "X:Y:Z"</param>
        /// <param name="sideLength">Длина стороны квадрата (в клетках)</param>
        /// <param name="locationsToRender">Полный список локаций для рендера</param>
        /// <param name="activeIds">Набор ID активных локаций</param>
        public async Task<Image> GenerateMapAround(string centerIndex, int sideLength, 
            IEnumerable<LocationData> locationsToRender, 
            HashSet<int> activeIds = null)
        {
            if (TargetViewport == null || ViewportRoot == null) return null;

            foreach (var child in ViewportRoot.GetChildren()) child.QueueFree();

            // 1. Строим карту локаций
            var cellToLocationId = BuildLocationMap(locationsToRender);

            // 2. Парсим центр
            var centerCoord = ParseStringCoordinate(centerIndex);
    
            // 3. Генерируем сетку
            var (bounds, gridMap) = GenerateGridSquare(centerCoord, sideLength);

            // ИЗМЕНЕНИЕ 1: Убираем "+ 2", ставим размер ровно по границам
            TargetViewport.Size = new Vector2I(bounds.Size.X * CellSize, bounds.Size.Y * CellSize);
            TargetViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

            var drawBounds = new Rect2I(bounds.Position, bounds.Size);

            // Функции доступа
            int GetLocId(string idx) => cellToLocationId.ContainsKey(idx) ? cellToLocationId[idx] : -1;
            bool IsOccupied(string idx) => cellToLocationId.ContainsKey(idx);
            bool IsActive(int id) => activeIds == null || activeIds.Contains(id);

            // ИЗМЕНЕНИЕ 2: Убираем смещение (paddingOffset: Vector2I.Zero)
            DrawMap(ViewportRoot, drawBounds, gridMap, GetLocId, IsOccupied, IsActive, paddingOffset: Vector2I.Zero);

            await ToSignal(GetTree(), "process_frame");
            await ToSignal(GetTree(), "process_frame");

            var img = TargetViewport.GetTexture().GetImage();

            if (AutoSave && img != null)
            {
                SaveMapToDisk(img);
            }

            return img;
        }

        /// <summary>
        /// Метод 2. Создание карты на основе списка активных локаций с учетом контекста мира.
        /// </summary>
        public async Task<Image> GenerateMap(IEnumerable<LocationData> activeLocations, IEnumerable<LocationData> allWorldLocations)
        {
            if (activeLocations == null || !activeLocations.Any()) return null;
            if (allWorldLocations == null) allWorldLocations = new List<LocationData>();

            // Получаем расширенную область
            var areaResult = WorldStateHelper.GetExtendedArea(activeLocations, 12, allWorldLocations.ToList());

            if (areaResult == null) return null;

            // Собираем полный список локаций для рендера
            var combinedLocations = activeLocations.Concat(areaResult.Locations).ToList();

            // ID активных локаций
            var activeIds = activeLocations.Select(l => l.Id).ToHashSet();

            // areaResult.Radius содержит длину стороны (согласно задаче)
            return await GenerateMapAround(
                areaResult.CenterCellIndex, 
                areaResult.SideLength, 
                combinedLocations, 
                activeIds
            );
        }

        // --- Helpers ---

        private Vector2I ParseStringCoordinate(string indexStr)
        {
            if (string.IsNullOrEmpty(indexStr)) return Vector2I.Zero;

            var parts = indexStr.Split(':');
            if (parts.Length >= 2)
            {
                if (int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                {
                    return new Vector2I(x, y);
                }
            }
            return Vector2I.Zero;
        }

        private Dictionary<string, int> BuildLocationMap(IEnumerable<LocationData> locations)
        {
            var map = new Dictionary<string, int>();

            if (locations != null)
            {
                foreach (var loc in locations)
                {
                    foreach (var rawIndex in loc.GetCellIndices())
                    {
                        var vec = ParseStringCoordinate(rawIndex);
                        string normalizedKey = $"{vec.X}:{vec.Y}";
                        map[normalizedKey] = loc.Id;
                    }
                }
            }

            return map;
        }

        private (Rect2I, Dictionary<Vector2I, string>) GenerateGridSquare(Vector2I center, int sideLength)
        {
            var map = new Dictionary<Vector2I, string>();
            
            // Вычисляем смещение от центра, чтобы получить квадрат со стороной sideLength
            int offset = sideLength / 2;
            
            int minX = center.X - offset;
            int maxX = minX + sideLength - 1; // -1, т.к. включительно
            
            int minY = center.Y - offset;
            int maxY = minY + sideLength - 1;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    var pos = new Vector2I(x, y);
                    string indexStr = $"{x}:{y}"; 
                    map[pos] = indexStr;
                }
            }

            var bounds = new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return (bounds, map);
        }

        public void SaveMapToDisk(Image img, string customName = null)
        {
            if (img == null) return;

            if (!DirAccess.DirExistsAbsolute(SaveDirectory))
            {
                DirAccess.MakeDirRecursiveAbsolute(SaveDirectory);
            }

            string fileName;
            if (!string.IsNullOrEmpty(customName))
            {
                fileName = customName.EndsWith(".png") ? customName : $"{customName}.png";
            }
            else
            {
                string timestamp = Time.GetDatetimeStringFromSystem().Replace(":", "-").Replace(" ", "_");
                fileName = $"map_{timestamp}.png";
            }

            string fullPath = SaveDirectory;
            if (!fullPath.EndsWith("/")) fullPath += "/";
            fullPath += fileName;

            Error err = img.SavePng(fullPath);
            
            if (err == Error.Ok)
            {
                GD.Print($"✅ Map saved successfully: {fullPath}");
            }
            else
            {
                GD.PrintErr($"❌ Failed to save map to {fullPath}. Error: {err}");
            }
        }

        private void DrawMap(Control root, Rect2I logicBounds, Dictionary<Vector2I, string> gridMap, 
                             Func<string, int> getLocationId, Func<string, bool> isOccupied,
                             Func<int, bool> isLocationActive,
                             Vector2I paddingOffset)
        {
            var bg = new ColorRect { Color = Colors.Black };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(bg);

            int logicMinX = logicBounds.Position.X;
            int logicMaxX = logicBounds.Position.X + logicBounds.Size.X;
            int logicMinY = logicBounds.Position.Y;
            int logicMaxY = logicBounds.Position.Y + logicBounds.Size.Y;

            int worldTopY = logicBounds.Position.Y + logicBounds.Size.Y - 1;

            for (int x = logicMinX; x < logicMaxX; x++)
            {
                for (int y = logicMinY; y < logicMaxY; y++)
                {
                    var gridPos = new Vector2I(x, y);

                    int relX = x - logicMinX;
                    int relY = worldTopY - y; 

                    float screenX = (relX + paddingOffset.X) * CellSize;
                    float screenY = (relY + paddingOffset.Y) * CellSize;

                    string indexStr = gridMap.ContainsKey(gridPos) ? gridMap[gridPos] : null;
                    DrawCell(root, new Vector2(screenX, screenY), gridPos, indexStr, gridMap, 
                        getLocationId, isOccupied, isLocationActive);
                }
            }
        }

        private void DrawCell(Control root, Vector2 screenPos, Vector2I gridPos, string currentIndexStr, 
                            Dictionary<Vector2I, string> gridMap, 
                            Func<string, int> getLocationId, Func<string, bool> isOccupied,
                            Func<int, bool> isLocationActive)
        {
            var cellContainer = new Control();
            cellContainer.Position = screenPos;
            cellContainer.Size = new Vector2(CellSize, CellSize);
            root.AddChild(cellContainer);

            bool currentOccupied = currentIndexStr != null && isOccupied(currentIndexStr);
            int currentLocationId = currentOccupied ? getLocationId(currentIndexStr) : -1;
            Color occupiedColor = currentOccupied ? GetLocationColor(currentLocationId) : Colors.White;
            
            bool isActive = isLocationActive(currentLocationId);

            if (currentOccupied && !isActive)
            {
                cellContainer.Modulate = new Color(1, 1, 1, 0.4f); 
            }

            var label = new Label();
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            label.ZIndex = 3; 
            label.AddThemeColorOverride("font_color", Colors.White);
            
            if (currentOccupied)
            {
                if (isActive)
                {
                    label.Text = $"{gridPos.X}:{gridPos.Y}";
                    label.AddThemeFontSizeOverride("font_size", 16);
                }
                else
                {
                    label.Text = $"ID = {currentLocationId}";
                    label.AddThemeFontSizeOverride("font_size", 14);
                }
            }
            else
            {
                label.Text = $"{gridPos.X}:{gridPos.Y}";
                label.AddThemeFontSizeOverride("font_size", 16);
            }

            cellContainer.AddChild(label);

            CheckBorder(cellContainer, Side.Top,    gridPos, new Vector2I(gridPos.X, gridPos.Y + 1), currentOccupied, currentLocationId, occupiedColor, gridMap, getLocationId, isOccupied);
            CheckBorder(cellContainer, Side.Bottom, gridPos, new Vector2I(gridPos.X, gridPos.Y - 1), currentOccupied, currentLocationId, occupiedColor, gridMap, getLocationId, isOccupied);
            CheckBorder(cellContainer, Side.Left,   gridPos, new Vector2I(gridPos.X - 1, gridPos.Y), currentOccupied, currentLocationId, occupiedColor, gridMap, getLocationId, isOccupied);
            CheckBorder(cellContainer, Side.Right,  gridPos, new Vector2I(gridPos.X + 1, gridPos.Y), currentOccupied, currentLocationId, occupiedColor, gridMap, getLocationId, isOccupied);
        }

        private enum Side { Top, Bottom, Left, Right }

        private void CheckBorder(Control parent, Side side, 
                               Vector2I currentPos, Vector2I neighborPos, 
                               bool currentOccupied, int currentLocId, Color currentColor,
                               Dictionary<Vector2I, string> gridMap, 
                               Func<string, int> getLocationId, Func<string, bool> isOccupied)
        {
            string neighborIndexStr = gridMap.ContainsKey(neighborPos) ? gridMap[neighborPos] : null;
            
            bool neighborOccupied = false;
            int neighborLocId = -1;

            if (neighborIndexStr != null)
            {
                neighborOccupied = isOccupied(neighborIndexStr);
                neighborLocId = neighborOccupied ? getLocationId(neighborIndexStr) : -1;
            }

            if (currentOccupied)
            {
                if (!neighborOccupied || neighborLocId != currentLocId)
                {
                    CreateBorderLine(parent, side, currentColor, 4, 2); 
                }
            }
            else 
            {
                if (!neighborOccupied)
                {
                    CreateBorderLine(parent, side, Colors.White, 1, 1); 
                }
            }
        }

        private void CreateBorderLine(Control parent, Side side, Color color, int thickness, int zIndex)
        {
            var lineNode = new ColorRect();
            lineNode.Color = color;
            lineNode.ZIndex = zIndex;
            parent.AddChild(lineNode);
            lineNode.SetAnchorsPreset(Control.LayoutPreset.TopLeft);

            switch (side)
            {
                case Side.Top:
                    lineNode.Position = new Vector2(0, 0);
                    lineNode.Size = new Vector2(CellSize, thickness);
                    break;
                case Side.Bottom:
                    lineNode.Position = new Vector2(0, CellSize - thickness);
                    lineNode.Size = new Vector2(CellSize, thickness);
                    break;
                case Side.Left:
                    lineNode.Position = new Vector2(0, 0);
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