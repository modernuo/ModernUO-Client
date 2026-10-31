# Architecture Recommendations

> **History.** Proposals of 2026-10-23, not decisions. The owner's decisions are in [README.md](README.md#decisions-so-far-from-the-project-owner); the asset pipeline (section 3's converter) is now specified in [data-formats.md](data-formats.md) and [pack-format.md](pack-format.md).

## 1. World storage: split static from dynamic

About 99% of world objects are immutable: land, statics and multis. Neither ClassicUO's one-heap-object-per-tile model nor one-entity-per-tile is right for them.

| Data | Storage | Why |
|---|---|---|
| Land, statics, multi components | **Chunk-packed struct-of-arrays plus persistent per-chunk GPU buffers** | Load and evict whole chunks; nothing to iterate per frame on the CPU. The chunk is the streaming unit. |
| Items, mobiles, effects, lights, particles' emitters | **ECS (or typed data-oriented stores)** keyed by serial | Thousands of objects that change constantly; composition helps for built-in assistant features. |
| UI | Retained widget tree (classic gumps translated into it) | UI isn't a throughput problem. |
| Network | Typed packet handlers writing into the stores/ECS | Imperative and serial-keyed by nature. |

With only dynamic objects in it, the ECS choice is about code structure, not speed. The benchmarks show every library is fast enough; see [ecs-benchmarks.md](ecs-benchmarks.md).

**ECS vs typed stores.** UO has about six fixed object kinds, so dense typed stores (`MobileStore`, `ItemStore`, `EffectStore`) reach the same performance ceiling with a lower learning curve. A generic ECS pays off when many cross-cutting features (built-in assistant behaviours, status effects, UI bindings) attach data to objects. Either way:

- **Single-threaded, deterministic system order by default.** Automatic parallel batching is where bugs live; TinyEcs's ordering bug is the proof.
- **Explicit parallel jobs** only for render extraction, asset decoding and chunk mesh building.

## 2. Asset converter and pack format (first deliverable)

An offline tool turns the user's UO files (UOP/MUL/verdata) into **content-addressed packs**:

- **Sprites:**
  - trimmed and packed into texture-array pages (e.g. 2048² layers) with gutters and mips;
  - compressed as KTX2/Basis Universal, which transcodes to BC7 (desktop) or ASTC (mobile) at load;
  - optional offline AI upscaling for HiDPI art.
- **Hues:** keep the shader lookup ClassicUO uses (grayscale → hue LUT), stored as a texture array.
- **Per-sprite metadata:** UV/layer, draw offset, bounds, hit mask, and generated **normal and height maps** for 2.5D lighting. Hand-author maps for key art if the generated ones aren't good enough.
- **Map:** chunked by (facet, block), with land, statics and multis pre-baked into GPU-ready vertex or instance data.
- **Audio:** WAV/MP3 → Ogg Vorbis or Opus.
- **Manifest:** hashes per pack and per chunk, so the CDN, the bundle and the local cache all use the same files, and patching only downloads what changed.

## 3. Streaming and residency

1. A sprite or chunk is requested and gets a handle immediately.
2. An IO thread reads from a local pack (memory-mapped) or the CDN (HTTP range requests).
3. Worker threads transcode or decode.
4. The render thread uploads through a staging ring buffer with a **per-frame upload budget**.
5. A placeholder or low mip is drawn until the asset is resident.
6. LRU eviction per texture page, with last-used tracking from the previous frame's visible set.
7. The map prefetches a ring of chunks around the player, biased toward the walking direction. Integration is cheap: spawning and despawning about 2.6k entities costs 0.09–0.15 ms.

## 4. Renderer (GPU-driven 2.5D)

- **Instances:** one 32-byte instance per sprite (position, size, layer/UV index, hue, depth key, flags), drawn with instancing or vertex pulling from a storage buffer. Texture arrays mean no flush on texture changes.
- **Static chunks** are resident GPU buffers; only dynamic instances are uploaded each frame.
- **Ordering:** integer isometric depth key computed in the shader; depth buffer for opaque sprites; one sorted pass for transparent ones.
- **Lighting:** deferred. The G-buffer holds albedo, normal and height; many point lights via a tiled or clustered pass; shadows ray-marched in screen space over the height buffer. No 3D geometry needed.
- **Particles:** GPU compute. **Picking:** an ID render target with an async 1-pixel readback.
- **Upscaling:**
  - Temporal upscalers (DLSS/FSR/XeSS) assume jittered 3D scenes and tend to smear pixel art.
  - Sprite drawing isn't GPU-heavy, so the big win is offline art upscaling plus integer scaling.
  - If lighting, shadows or particles at 4K become expensive, spatial upscaling (FSR1-style) of those passes is the safe option.
  - DLSS would need D3D12/Vulkan plus NVIDIA's native SDK.
- Graphics API: see [graphics-api.md](graphics-api.md).

## 5. Protocol, prediction, rollback

- **Lean delta protocol.** Use Span-based zero-allocation parsing and writing, the same approach as ModernUO's `SpanReader`/`SpanWriter`.
- **Predict only the local player.** Walk immediately against local walkability data (tiledata flags, statics, multis), then reconcile when the server confirms or rejects each movement sequence. Remote mobiles are interpolated from timestamps.
- **Share the movement and collision rules in a C# library used by both ModernUO and the client.** That keeps prediction from drifting.
- **Rollback is cheap:** snapshotting 5k mobiles costs about 7 µs. Full-world rollback isn't needed.
- **GC:** struct components, pooled buffers, `GCSettings.LatencyMode = SustainedLowLatency` during gameplay.

## 6. Anti-cheat and built-in assistant features

- **Build assistant features in:** hotkeys, an organizer, dress/undress, counters, journal filters, healthbar management, and single-press action chains with no loops or conditionals.
- **Remove the hook points:**
  - no plugin loader (drop `cuoapi`, Razor hooks and the plugin interface);
  - a NativeAOT binary, so no IL patching or Harmony/reflection injection;
  - an encrypted, session-keyed protocol.
- **Limits:** the client is untrusted, and memory editing or injection can only be made harder. Real enforcement is on the server: action rate limits, validation of every request, and detection of bot-like timing.
- **Optional:** data-only cosmetic customization (UI layouts and skins) with no code execution.

## 7. UI

- Classic gumps translated into the same retained widget tree.
- **Server-driven modern UI:** the server sends a schema'd declarative layout tree, data deltas update bound fields, and user actions return as typed messages.
- **Text:** a glyph atlas covering UO bitmap fonts plus TTF with font fallback. Add HarfBuzz shaping and bidi when we localize to complex or right-to-left scripts.
- **IME** (the OS composition window for Chinese, Japanese and Korean): SDL3 delivers text-editing events, and the text box renders the composition string. Needed only for CJK.
- **Controllers:** SDL3 gamepad input. The work is the UX: target cycling (nearest hostile, next item), a virtual cursor, radial menus, UI focus navigation.

## 8. Platforms

- **Desktop:** NativeAOT on Windows, Linux and macOS.
- **Steam Deck:**
  - native Linux + Vulkan build;
  - UI scaling for 1280×800;
  - controller-first UX;
  - Steam on-screen keyboard (SDL can request it);
  - readable text at 800p.
- **Browser:**
  - Official .NET browser builds run on Mono-WASM, not NativeAOT. Keep core assemblies (simulation, protocol, stores, rules) pure, trim-safe .NET with no P/Invoke.
  - Put windowing, input, audio, GPU, file and socket access behind a platform layer.
  - Make the simulation correct single-threaded; browser threading is limited.
  - ModernUO must accept WebSocket or WebTransport connections.
- **Mobile:** same platform layer; ASTC textures; touch UX is its own project.

## Suggested next steps

1. Agree on the static/dynamic split and a single-threaded-by-default ECS.
2. Build the converter and pack format.
3. Vertical slice: a GPU-driven sprite renderer drawing one facet region from packs, with streaming. Measure it against ClassicUO.
4. Pick the ECS library inside that slice: Friflo first, TinyEcs (forked) second.
5. Shared movement/collision rules library with ModernUO, then client-side prediction.
