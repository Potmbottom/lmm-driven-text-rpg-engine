using Godot;
using RPG.Core;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RPG.Tools
{
    public partial class MapGenerator : Node
    {
        [Export] public int CellSize = 128;
        [Export] public SubViewport TargetViewport;
        [Export] public Control ViewportRoot;

        public override void _Ready()
        {
            if (TargetViewport == null || ViewportRoot == null)
            {
                GD.PrintErr("MapGenerator: Viewport or Root not assigned!");
            }
        }

        public async Task<Image> GenerateMapImage(List<string> freeCells, List<string> occupiedCells, Rect2I bounds)
        {
            if (TargetViewport == null) return null;

            foreach (var child in ViewportRoot.GetChildren()) child.QueueFree();

            TargetViewport.Size = new Vector2I(bounds.Size.X * CellSize, bounds.Size.Y * CellSize);
            TargetViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

            DrawMap(ViewportRoot, freeCells, occupiedCells, bounds);

            TargetViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

            await ToSignal(GetTree(), "process_frame");
            await ToSignal(GetTree(), "process_frame");
            await ToSignal(GetTree(), "process_frame");

            return TargetViewport.GetTexture().GetImage();
        }

        private void DrawMap(Control root, List<string> freeCells, List<string> occupiedCells, Rect2I bounds)
        {
            // Background
            var bg = new ColorRect { Color = Colors.Black };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(bg);

            // Draw Free Cells
            foreach (var cellIndex in freeCells)
                DrawCell(root, cellIndex, bounds, Colors.White, isOccupied: false);

            // Draw Occupied Cells
            foreach (var cellIndex in occupiedCells)
                DrawCell(root, cellIndex, bounds, Colors.Red, isOccupied: true);
        }

        private void DrawCell(Control root, string indexStr, Rect2I bounds, Color borderColor, bool isOccupied)
        {
            var coord = GridCoordinate.Parse(indexStr);

            int drawX = (coord.X - bounds.Position.X) * CellSize;
            // Сохраняем правильную ориентацию Y (вверх)
            int maxY = bounds.Position.Y + bounds.Size.Y - 1;
            int drawY = (maxY - coord.Y) * CellSize;

            Vector2 pos = new Vector2(drawX, drawY);

            // 1. Создаем корневой контейнер для ячейки (Прозрачный, просто держит позицию и размер)
            var cellRoot = new Control();
            cellRoot.Position = pos;
            cellRoot.Size = new Vector2(CellSize, CellSize);
            // Важно: Z-Index ставим здесь, чтобы вся ячейка (и рамка, и текст) рисовалась в правильном порядке
            // Текст занятых ячеек будет поверх рамок свободных
            cellRoot.ZIndex = isOccupied ? 1 : 0; 
            root.AddChild(cellRoot);

            // 2. ВИЗУАЛ (Фон и Рамка) - Panel
            // Добавляем как дочерний элемент, но НЕ кладем текст внутрь него
            var panel = new Panel();
            panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            panel.MouseFilter = Control.MouseFilterEnum.Ignore;
            
            var style = new StyleBoxFlat();
            style.DrawCenter = true;

            if (isOccupied)
            {
                style.BgColor = new Color(0.2f, 0, 0, 1);
                style.BorderColor = borderColor;
                
                // Толстая граница
                int borderW = 4;
                style.BorderWidthBottom = borderW;
                style.BorderWidthTop = borderW;
                style.BorderWidthLeft = borderW;
                style.BorderWidthRight = borderW;

                // Расширяем рамку наружу, чтобы перекрыть соседей
                int expand = 2;
                style.ExpandMarginBottom = expand;
                style.ExpandMarginTop = expand;
                style.ExpandMarginLeft = expand;
                style.ExpandMarginRight = expand;
            }
            else
            {
                style.BgColor = Colors.Black;
                style.BorderColor = new Color(0.3f, 0.3f, 0.3f);
                
                int borderW = 1;
                style.BorderWidthBottom = borderW;
                style.BorderWidthTop = borderW;
                style.BorderWidthLeft = borderW;
                style.BorderWidthRight = borderW;
            }
            
            panel.AddThemeStyleboxOverride("panel", style);
            cellRoot.AddChild(panel); // Добавляем панель в корень ячейки

            // 3. КОНТЕНТ (Текст)
            // Создаем отдельно от панели, чтобы толщина границ панели не сдвигала текст
            var vbox = new VBoxContainer();
            vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect); // Растягиваем на всю ячейку
            vbox.Alignment = BoxContainer.AlignmentMode.Center;
            vbox.AddThemeConstantOverride("separation", 0);
            
            // Чтобы текст наверняка был поверх любых рамок (даже своей собственной)
            // хотя порядок добавления (AddChild после панели) обычно решает это.
            // Но на всякий случай можно оставить ZIndex панели, а vbox оставить как есть.
            
            cellRoot.AddChild(vbox); 
            
            // Label Y
            var labelY = new Label();
            labelY.Text = coord.Y.ToString();
            labelY.HorizontalAlignment = HorizontalAlignment.Center;
            labelY.VerticalAlignment = VerticalAlignment.Center;
            labelY.AddThemeColorOverride("font_color", Colors.White);
            labelY.AddThemeFontSizeOverride("font_size", 24);
            vbox.AddChild(labelY);

            // Separator
            var separator = new ColorRect();
            separator.Color = isOccupied ? new Color(1, 0.3f, 0.3f) : new Color(0.3f, 0.3f, 0.3f);
            separator.CustomMinimumSize = new Vector2(CellSize * 0.6f, 2); // 60% ширины
            separator.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            vbox.AddChild(separator);
            
            // Label X
            var labelX = new Label();
            labelX.Text = coord.X.ToString();
            labelX.HorizontalAlignment = HorizontalAlignment.Center;
            labelX.VerticalAlignment = VerticalAlignment.Center;
            labelX.AddThemeColorOverride("font_color", Colors.White);
            labelX.AddThemeFontSizeOverride("font_size", 24); // Чуть уменьшил шрифт для безопасности
            vbox.AddChild(labelX);
        }
    }
}