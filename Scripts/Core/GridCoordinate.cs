using Godot;
using System;

namespace RPG.Core
{
    public struct GridCoordinate
    {
        public int X;
        public int Y;
        public int Z;

        public GridCoordinate(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static GridCoordinate Parse(string indexStr)
        {
            var vec = ParseVector(indexStr);
            return new GridCoordinate(vec.X, vec.Y, 0);
        }
        
        public static Vector2I ParseVector(string indexStr)
        {
            if (string.IsNullOrEmpty(indexStr)) return Vector2I.Zero;

            var parts = indexStr.Split(':');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y))
            {
                return new Vector2I(x, y);
            }
            return Vector2I.Zero;
        }
        
        public override string ToString()
        {
            return $"{X}:{Y}:{Z}";
        }
        
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override bool Equals(object obj) => obj is GridCoordinate other && this == other;
        public static bool operator ==(GridCoordinate a, GridCoordinate b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        public static bool operator !=(GridCoordinate a, GridCoordinate b) => !(a == b);
    }
}