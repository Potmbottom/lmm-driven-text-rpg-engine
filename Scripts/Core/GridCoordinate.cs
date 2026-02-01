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
        
        public static GridCoordinate Parse(string index)
        {
            var parts = index.Split(':');
            if (parts.Length != 3) throw new FormatException($"Invalid coordinate format: {index}");
            return new GridCoordinate(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
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