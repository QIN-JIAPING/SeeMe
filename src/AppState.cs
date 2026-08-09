// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Windows;

namespace SeeMe
{
    /// <summary>
    /// 应用级状态容器：集中持有左右两个面板状态、文件历史与分栏模式。
    /// 实现 INotifyPropertyChanged，供 UI 绑定；外部不再直接修改 PanelState 字段，统一经此聚合对象访问。
    /// </summary>
    internal class AppState : INotifyPropertyChanged
    {
        private PanelState _left = null!;
        private PanelState _right = null!;
        private FileHistory _history = null!;
        private bool _isSplitMode;

        public PanelState Left
        {
            get => _left;
            set { _left = value; OnPropertyChanged(nameof(Left)); }
        }

        public PanelState Right
        {
            get => _right;
            set { _right = value; OnPropertyChanged(nameof(Right)); }
        }

        public FileHistory History
        {
            get => _history;
            set { _history = value; OnPropertyChanged(nameof(History)); }
        }

        public bool IsSplitMode
        {
            get => _isSplitMode;
            set
            {
                if (_isSplitMode == value) return;
                _isSplitMode = value;
                OnPropertyChanged(nameof(IsSplitMode));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
