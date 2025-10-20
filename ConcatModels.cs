using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using WinUIShared.Enums;

namespace ConcatMediaPage
{
    public class MainModel: INotifyPropertyChanged
    {
        private ObservableCollection<ConcatItem> _items;
        public ObservableCollection<ConcatItem> Items
        {
            get => _items;
            set
            {
                if (SetProperty(ref _items, value, alsoNotify: [nameof(HasItems), nameof(CanConcat)]))
                {
                    _items.CollectionChanged += (_, _) =>
                    {
                        OnPropertyChanged(nameof(HasItems));
                        OnPropertyChanged(nameof(CanConcat));
                    };
                }
            }
        }

        private OperationState _state;
        public OperationState State
        {
            get => _state;
            set => SetProperty(ref _state, value, alsoNotify: [nameof(BeforeOperation), nameof(DuringOperation), nameof(AfterOperation), nameof(CanConcat)]);
        }

        public bool HasItems => _items.Count > 0;
        public bool CanConcat => _items.Count > 1 && !DuringOperation;
        public bool BeforeOperation => State == OperationState.BeforeOperation;
        public bool DuringOperation => State == OperationState.DuringOperation;
        public bool AfterOperation => State == OperationState.AfterOperation;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null, params string[] alsoNotify)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            foreach (var dep in alsoNotify) OnPropertyChanged(dep);
            return true;
        }
    }

    public class ConcatItem : INotifyPropertyChanged
    {
        private string _filepath;
        public string FilePath
        {
            get => _filepath;
            set => SetProperty(ref _filepath, value);
        }
        private string _filename;
        public string FileName
        {
            get => _filename;
            set => SetProperty(ref _filename, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null, params string[] alsoNotify)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            foreach (var dep in alsoNotify) OnPropertyChanged(dep);
            return true;
        }
    }
}
