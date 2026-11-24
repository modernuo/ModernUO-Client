using ClassicUO.Assets;

namespace ClassicUO.Renderer.Animations
{
    public sealed partial class Animations
    {
        /// <summary>Whether a direction load goes to a worker: never for a sync caller (picking) or with chunks already
        /// cached (no decompress); a first sight, an evicted direction and a turn or new action of a body already drawn
        /// otherwise (a turn was a main-thread decode: Legacy p90 ~17 ms, bursts of 16+ ms).</summary>
        public static bool DecodesOnWorker(bool sync, bool resident) => AsyncFirstSight && !sync && !resident;

        /// <summary>
        /// The turn prefetch's early request (a direction a mobile will likely draw next): a direction never loaded is
        /// decoded on a worker even when its chunks are cached, so a prefetch never decodes on the main thread. A loaded,
        /// pending or evicted direction is left alone (the draw reloads an evicted one, with the LRU's accounting).
        /// </summary>
        public void PrefetchDirection(ushort id, byte action, byte dir, bool isEquip)
        {
            if (action >= AnimationsLoader.MAX_ACTIONS || dir >= AnimationsLoader.MAX_DIRECTIONS || id >= _animationLoader.BodyCount)
            {
                return;
            }

            if (_queue.HasReady)
            {
                DrainReady();
            }

            id = _animationLoader.ResolveBody(id, false, out _);
            var type = (AnimationGroupsType)_animationLoader.GetBody(id).Type;

            if (isEquip && type != AnimationGroupsType.Equipment && type != AnimationGroupsType.Human)
            {
                return;
            }

            ref var actions = ref _cache[id];
            actions ??= new SpriteInfo[AnimationsLoader.MAX_ACTIONS][][];
            ref var dirs = ref actions[action];
            dirs ??= new SpriteInfo[AnimationsLoader.MAX_DIRECTIONS][];

            if (dirs[dir] == null && (_evictedAt.Count == 0 || !_evictedAt.ContainsKey(DirKey(id, action, dir))))
            {
                dirs[dir] = LoadDirection(id, action, dir, sync: false, prefetch: true);
            }
        }
    }
}
