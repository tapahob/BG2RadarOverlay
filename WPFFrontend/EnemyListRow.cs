using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WPFFrontend
{
    /// <summary>
    /// A lightweight, mutable view-model for one row in the nearby-enemies ListView (MainWindow's
    /// StackPanel/ListView), keyed by BGEntity.tag.
    ///
    /// The polling loop's BGEntity objects are recreated fresh every tick (RefreshedCopy() hands
    /// back a new instance rather than mutating a cached one - see BGEntity's own comments), so
    /// they can't carry cross-tick UI state themselves. This row is instead created once per
    /// on-screen enemy and updated in place (Name/CurrentHP setters raise PropertyChanged) as
    /// long as that enemy stays in the nearest-enemies set, which is what lets WPF reuse the same
    /// ListViewItem/HpRollText across ticks instead of tearing the row down and rebuilding it -
    /// necessary for HpRollText's roll-down animation to have an "old value" to animate from.
    /// </summary>
    public class EnemyListRow : INotifyPropertyChanged
    {
        public int Tag { get; }

        private string _name;
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                OnPropertyChanged();
            }
        }

        private int _currentHP;
        public int CurrentHP
        {
            get => _currentHP;
            set
            {
                if (_currentHP == value) return;
                _currentHP = value;
                OnPropertyChanged();
            }
        }

        public EnemyListRow(int tag, string name, int currentHP)
        {
            Tag = tag;
            _name = name;
            _currentHP = currentHP;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
