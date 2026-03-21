using System;
using island.Models;

namespace island.Services
{
    /// <summary>
    /// Pure logic controller for the Island's state machine and physics.
    /// No WinUI dependency, just math and logical states.
    /// </summary>
    public class IslandController
    {
        // --- Logical Inputs ---
        public bool IsHovered { get; set; }
        public bool IsDragging { get; set; }
        public bool IsDocked { get; set; }
        public bool IsNotifying { get; set; }
        public bool IsForegroundMaximized { get; set; }
        public bool IsHoverPending { get; set; }

        // --- Current Value Outputs ---
        public IslandState Current { get; } = new();

        // --- Target Values ---
        private double _targetWidth = IslandConfig.CompactWidth;
        private double _targetHeight = IslandConfig.CompactHeight;
        private double _targetY = IslandConfig.DefaultY;
        public double TargetY => _targetY;
        private double _targetCompactOpacity = 1.0;
        private double _targetExpandedOpacity = 0.0;

        public IslandController()
        {
            Current.Width = IslandConfig.CompactWidth;
            Current.Height = IslandConfig.CompactHeight;
            Current.Y = IslandConfig.DefaultY;
            Current.CompactOpacity = 1.0;
        }

        public void InitializePosition(double centerX, double y, bool isDocked)
        {
            Current.CenterX = centerX;
            Current.Y = y;
            _targetY = y;
            IsDocked = isDocked;
        }

        /// <summary>
        /// Recalculate targets based on logical state.
        /// </summary>
        public void UpdateTargetState()
        {
            if (IsNotifying)
            {
                // Respect floating position if not docked
                SetExpandedTargets(IsDocked ? 0 : Current.Y);
            }
            else if (IsHovered && !IsDragging)
            {
                // Respect floating position if not docked
                SetExpandedTargets(IsDocked ? 0 : Current.Y);
            }
            else
            {
                SetCompactTargets();
            }
        }

        private void SetExpandedTargets(double y)
        {
            _targetWidth = IslandConfig.ExpandedWidth;
            _targetHeight = IslandConfig.ExpandedHeight;
            _targetY = y;
            _targetCompactOpacity = 0;
            _targetExpandedOpacity = 1;
        }

        private void SetCompactTargets()
        {
            _targetWidth = IslandConfig.CompactWidth;
            _targetHeight = IslandConfig.CompactHeight;
            _targetCompactOpacity = 1;
            _targetExpandedOpacity = 0;

            if (IsDocked && !IsDragging)
            {
                // In refactored sync layer (MainWindow), we use precision height. 
                // But we still need a logical target to drive the animation.
                double peek = IsForegroundMaximized ? IslandConfig.MaximizedDockPeekOffset : IslandConfig.DockPeekOffset;
                _targetY = -_targetHeight + peek;
            }
            else if (!IsDragging)
            {
                _targetY = Math.Max(IslandConfig.DefaultY, Current.Y);
            }
        }

        /// <summary>
        /// Update physics/animation state for one frame.
        /// </summary>
        public void Tick(double dt)
        {
            if (dt <= 0) return;
            double t = 1.0 - Math.Exp(-IslandConfig.AnimationSpeed * dt);

            Current.Width += (_targetWidth - Current.Width) * t;
            Current.Height += (_targetHeight - Current.Height) * t;
            Current.CompactOpacity += (_targetCompactOpacity - Current.CompactOpacity) * t;
            Current.ExpandedOpacity += (_targetExpandedOpacity - Current.ExpandedOpacity) * t;

            if (!IsDragging)
            {
                Current.Y += (_targetY - Current.Y) * t;
            }

            Current.IsHitTestVisible = Math.Max(Current.CompactOpacity, Current.ExpandedOpacity) > IslandConfig.HitTestOpacityThreshold;
        }

        // --- Drag Helpers ---
        public void HandleDrag(double centerX, double y)
        {
            Current.CenterX = centerX;
            Current.Y = y;

            if (Current.Y <= IslandConfig.DockThreshold)
            {
                Current.Y = 0;
            }
            else
            {
                // Real-time dock release to allow freedom of movement (Fixes "stuck at top" issue)
                IsDocked = false;
            }
            
            UpdateTargetState();
        }

        public void FinalizeDrag()
        {
            IsDocked = Current.Y <= IslandConfig.DockThreshold;
            
            // Set the final rest position as the new target
            if (!IsDocked)
            {
                _targetY = Math.Max(IslandConfig.DefaultY, Current.Y);
            }
            
            UpdateTargetState();
        }

        // --- Hidden Check ---
        public bool IsOffscreen()
        {
            return IsDocked && IsForegroundMaximized && !IsHovered && !IsNotifying && !IsDragging 
                   && Current.Y < -_targetHeight + 2;
        }
    }
}
