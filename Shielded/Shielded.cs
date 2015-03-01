using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shielded
{
    /// <summary>
    /// Makes your data thread safe. Works with structs, or simple value types,
    /// and the language does the necessary cloning. If T is a class, then only
    /// the reference itself is protected.
    /// </summary>
    public class Shielded<T> : ICommutableShielded
    {
        private class ValueKeeper
        {
            public long Version;
            public T Value;
            public ValueKeeper Older;
        }
        
        private ValueKeeper _current;
        // once negotiated, kept until commit or rollback
        private volatile WriteStamp _writerStamp;
        private readonly LocalStorage<ValueKeeper> _locals = new LocalStorage<ValueKeeper>();
        private readonly StampLocker _locker = new StampLocker();
        private readonly object _owner;

        /// <summary>
        /// Constructs a new Shielded container, containing default value of type T.
        /// </summary>
        public Shielded(object owner = null)
        {
            _current = new ValueKeeper();
            _owner = owner ?? this;
        }

        /// <summary>
        /// Constructs a new Shielded container, containing the given initial value.
        /// </summary>
        public Shielded(T initial, object owner = null)
        {
            _current = new ValueKeeper();
            _current.Value = initial;
            _owner = owner ?? this;
        }

        bool LockCheck()
        {
            var w = _writerStamp;
            return w == null || w.Version == null || w.Version > Shield.ReadStamp;
        }

        /// <summary>
        /// Enlists the field in the current transaction and, if this is the first
        /// access, checks the write lock. Will spin-wait (or Monitor.Wait if SERVER
        /// is defined) if the write stamp &lt;= <see cref="Shield.ReadStamp"/>, until
        /// write lock is released. Since write stamps are increasing, this is
        /// likely to happen only at the beginning of transactions.
        /// </summary>
        private void CheckLockAndEnlist()
        {
            // if already enlisted, no need to check lock.
            if (!Shield.Enlist(this, _locals.HasValue))
                return;

            if (!LockCheck())
                _locker.WaitUntil(LockCheck);
        }

        private ValueKeeper CurrentTransactionOldValue()
        {
            var point = _current;
            while (point.Version > Shield.ReadStamp)
                point = point.Older;
            return point;
        }

        /// <summary>
        /// Gets the value that this Shielded contained at transaction opening. During
        /// a transaction, this is constant.
        /// </summary>
        public T GetOldValue()
        {
            CheckLockAndEnlist();
            return CurrentTransactionOldValue().Value;
        }

        private void PrepareForWriting(bool prepareOld)
        {
            CheckLockAndEnlist();
            if (_current.Version > Shield.ReadStamp)
                throw new TransException("Write collision.");
            if (!_locals.HasValue)
            {
                var v = new ValueKeeper();
                if (prepareOld)
                    v.Value = CurrentTransactionOldValue().Value;
                _locals.Value = v;
            }
        }

        /// <summary>
        /// Reads or writes into the content of the field. Reading can be
        /// done out of transaction, but writes must be inside.
        /// </summary>
        public T Value
        {
            get
            {
                if (!Shield.IsInTransaction)
                    return _current.Value;

                CheckLockAndEnlist();
                if (!_locals.HasValue)
                    return CurrentTransactionOldValue().Value;
                else if (_current.Version > Shield.ReadStamp)
                    throw new TransException("Writable read collision.");
                return _locals.Value.Value;
            }
            set
            {
                PrepareForWriting(false);
                _locals.Value.Value = value;
                Changed.Raise(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Delegate type used for modifications, i.e. read and write operations.
        /// It has the advantage of working directly on the internal, thread-local
        /// storage copy, to which it gets a reference. This is more efficient if
        /// the type T is a big value-type.
        /// </summary>
        public delegate void ModificationDelegate(ref T value);

        /// <summary>
        /// Modifies the content of the field, i.e. read and write operation.
        /// It has the advantage of working directly on the internal, thread-local
        /// storage copy, to which it gets a reference. This is more efficient if
        /// the type T is a big value-type.
        /// </summary>
        public void Modify(ModificationDelegate d)
        {
            PrepareForWriting(true);
            d(ref _locals.Value.Value);
            Changed.Raise(this, EventArgs.Empty);
        }

        /// <summary>
        /// The action is performed just before commit, and reads the latest
        /// data. If it conflicts, only it is retried. If it succeeds,
        /// we (try to) commit with the same write stamp along with it.
        /// But, if you access this Shielded, it gets executed directly in this transaction.
        /// The Changed event is raised only when the commute is enlisted, and not
        /// when (and every time, given possible repetitions..) it executes.
        /// </summary>
        public void Commute(ModificationDelegate perform)
        {
            Shield.EnlistStrictCommute(() => {
                PrepareForWriting(true);
                perform(ref _locals.Value.Value);
            }, this);
            Changed.Raise(this, EventArgs.Empty);
        }

        /// <summary>
        /// For use by the ProxyGen, since the users of it know only about the base type used
        /// to generate the proxy. The struct used internally is not exposed, and so users
        /// of proxy classes could not write a ModificationDelegate which works on an argument
        /// whose type is that hidden struct.
        /// </summary>
        public void Commute(Action perform)
        {
            Shield.EnlistStrictCommute(perform, this);
            Changed.Raise(this, EventArgs.Empty);
        }

        /// <summary>
        /// Event raised after any change, and directly in the transaction that changed it.
        /// Subscriptions are transactional. In case of a commute, event is raised immediately
        /// after the commute is enlisted, and your handler can easily cause commutes to
        /// degenerate.
        /// </summary>
        public ShieldedEvent<EventArgs> Changed
        {
            get
            {
                if (_changed == null)
                    Interlocked.CompareExchange(ref _changed, new ShieldedEvent<EventArgs>(), null);
                return _changed;
            }
        }
        private ShieldedEvent<EventArgs> _changed;

        public static implicit operator T(Shielded<T> obj)
        {
            return obj.Value;
        }

        bool IShielded.HasChanges
        {
            get
            {
                return _locals.HasValue;
            }
        }

        object IShielded.Owner
        {
            get
            {
                return _owner;
            }
        }

        bool IShielded.CanCommit(WriteStamp writeStamp)
        {
            var res = _writerStamp == null &&
                _current.Version <= Shield.ReadStamp;
            if (res && _locals.HasValue)
                _writerStamp = writeStamp;
            return res;
        }
        
        void IShielded.Commit()
        {
            if (!_locals.HasValue)
                return;
            var newCurrent = _locals.Value;
            newCurrent.Older = _current;
            newCurrent.Version = _writerStamp.Version.Value;
            _current = newCurrent;
            _locals.Value = null;
            _writerStamp = null;
            _locker.Release();
        }

        void IShielded.Rollback()
        {
            if (!_locals.HasValue)
                return;
            _locals.Value = null;
            var ws = _writerStamp;
            if (ws != null && ws.ThreadId == Thread.CurrentThread.ManagedThreadId)
            {
                _writerStamp = null;
                _locker.Release();
            }
        }
        
        void IShielded.TrimCopies(long smallestOpenTransactionId)
        {
            // NB the "smallest transaction" and others can freely read while
            // we're doing this.
            var point = _current;
            while (point.Version > smallestOpenTransactionId)
                point = point.Older;
            // point is the last accessible - his Older is not needed.
            point.Older = null;
        }
    }
}

