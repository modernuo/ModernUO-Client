// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace ClassicUO.Loop
{
    /// <summary>
    /// GameController.EnqueueAction's list: actions that run once the game time is past their due tick.
    /// </summary>
    internal sealed class QueuedActions
    {
        private readonly List<(uint Time, Action Fn)> _items = new List<(uint, Action)>();

        public int Count => _items.Count;

        public void Add(uint dueTick, Action fn) => _items.Add((dueTick, fn));

        /// <summary>
        /// Runs the actions whose due tick is before <paramref name="now"/>, newest first. With <paramref name="drainAll"/> false
        /// only the first one found runs (the legacy loop: one per Update, which was ~1,000 a second); the event loop runs one
        /// Update per frame or wake, so it runs every due one. An action queued by a running action waits for the next call.
        /// </summary>
        public int RunDue(uint now, bool drainAll)
        {
            int ran = 0;

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (i >= _items.Count)
                {
                    continue;
                }

                (uint time, Action fn) = _items[i];

                if (now > time)
                {
                    fn();
                    _items.RemoveAt(i); // anything fn queued went after i, so i is still this action
                    ran++;

                    if (!drainAll)
                    {
                        break;
                    }
                }
            }

            return ran;
        }

        /// <summary>The earliest due tick, if any.</summary>
        public bool TryEarliest(out uint dueTick)
        {
            dueTick = uint.MaxValue;

            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Time < dueTick)
                {
                    dueTick = _items[i].Time;
                }
            }

            return _items.Count > 0;
        }
    }
}
