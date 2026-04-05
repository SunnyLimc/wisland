using System;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services
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
        public bool IsTransientSurfaceOpen { get; set; }

        // --- Current Value Outputs ---
        public IslandState Current { get; } = new();

        // --- Target Values ---
        private double _targetWidth = IslandConfig.CompactWidth;
        private double _targetHeight = IslandConfig.CompactHeight;
        private double _targetY = IslandConfig.DefaultY;
        public double TargetY => _targetY;
        private double _targetCompactOpacity = 1.0;
        private double _targetExpandedOpacity = 0.0;
        private bool _lastLoggedDockState;
        private double _lastLoggedTargetWidth;
        private double _lastLoggedTargetHeight;

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
            _lastLoggedDockState = isDocked;
            Logger.Debug($"Position initialized: CenterX={centerX:F1}, Y={y:F1}, IsDocked={isDocked}");
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
            else if ((IsHovered || IsTransientSurfaceOpen) && !IsDragging)
            {
                // Respect floating position if not docked
                SetExpandedTargets(IsDocked ? 0 : Current.Y);
            }
            else
            {
                SetCompactTargets();
            }

            if (Logger.IsEnabled(Helpers.LogLevel.Debug))
            {
                if (Math.Abs(_targetWidth - _lastLoggedTargetWidth) > 1.0
                    || Math.Abs(_targetHeight - _lastLoggedTargetHeight) > 1.0)
                {
                    Logger.Debug($"Target state: W={_targetWidth:F0}x H={_targetHeight:F0}, Y={_targetY:F1}, CompactOp={_targetCompactOpacity:F2}, ExpandedOp={_targetExpandedOpacity:F2}");
                    _lastLoggedTargetWidth = _targetWidth;
                    _lastLoggedTargetHeight = _targetHeight;
                }
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

        public void SnapToTargetState()
        {
            Current.Width = _targetWidth;
            Current.Height = _targetHeight;
            Current.Y = _targetY;
            Current.CompactOpacity = _targetCompactOpacity;
            Current.ExpandedOpacity = _targetExpandedOpacity;
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
                if (IsDocked)
                {
                    Logger.Debug($"Dock released during drag at Y={y:F1}");
                }
                // Real-time dock release to allow freedom of movement (Fixes "stuck at top" issue)
                IsDocked = false;
            }

            UpdateTargetState();
        }

        public void FinalizeDrag()
        {
            bool wasDocked = IsDocked;
            IsDocked = Current.Y <= IslandConfig.DockThreshold;

            // Set the final rest position as the new target
            if (!IsDocked)
            {
                _targetY = Math.Max(IslandConfig.DefaultY, Current.Y);
            }

            if (wasDocked != IsDocked)
            {
                Logger.Debug($"Dock state changed: {wasDocked} -> {IsDocked} at Y={Current.Y:F1}");
            }
            _lastLoggedDockState = IsDocked;
            Logger.Debug($"Drag finalized: CenterX={Current.CenterX:F1}, Y={Current.Y:F1}, IsDocked={IsDocked}");

            UpdateTargetState();
        }

        public bool HasPendingAnimation(double positionEpsilon = 0.25, double opacityEpsilon = 0.02)
        {
            return IsDragging
                || Math.Abs(Current.Width - _targetWidth) > positionEpsilon
                || Math.Abs(Current.Height - _targetHeight) > positionEpsilon
                || Math.Abs(Current.Y - _targetY) > positionEpsilon
                || Math.Abs(Current.CompactOpacity - _targetCompactOpacity) > opacityEpsilon
                || Math.Abs(Current.ExpandedOpacity - _targetExpandedOpacity) > opacityEpsilon;
        }

        // --- Hidden Check ---
        public bool IsOffscreen()
        {
            return IsDocked && IsForegroundMaximized && !IsHovered && !IsTransientSurfaceOpen && !IsNotifying && !IsDragging
                   && Current.Y < -_targetHeight + 2;
        }
    }
}
