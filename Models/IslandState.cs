namespace wisland.Models
{
    /// <summary>
    /// Represents the visual state of the Island at a specific point in time.
    /// </summary>
    public class IslandState
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Y { get; set; }
        public double CenterX { get; set; }
        
        public double CompactOpacity { get; set; }
        public double ExpandedOpacity { get; set; }
        
        public bool IsHitTestVisible { get; set; }
    }
}
