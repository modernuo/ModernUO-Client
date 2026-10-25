# Visual Features Roadmap

Drafted 2026-10-24 (owner request). These are the "beyond ClassicUO" visuals from the owner's original goals: 2.5D lights, normals, shadows, zoom, HiDPI and particles. They are **scheduled after ECS steps W1–W3** (world-prepare-ecs-plan.md), because effects attach to entities the ECS will own.

All of it lives in **our world renderer** (Uo.Render: HLSL, DXC, SDL_GPU). None of it needs FNA to go away (graphics-ownership-plan.md, Tiers 1–4 deferred). The light pass is the exception: it moves from FNA into Uo.Render as part of V2.

## Principles

1. **Vanilla is always available.** A "Classic" preset turns every enhancement off and must look like ClassicUO today: pixel parity with the Legacy screenshots. Every feature is a player-side toggle.
2. **Presets plus per-feature toggles:** Classic, Enhanced and Custom, with each feature switchable in Custom. Which preset is the default for new players is the owner's call (or the shard's; see below).
3. **The server can trigger, the player can decline.**
   - New packets let a shard start effects, sequences and atmosphere.
   - The client always honours the player's toggles.
   - Shards must never put gameplay-critical information only in an enhanced visual. Every server-triggered effect carries a classic fallback (an existing 2D effect, or nothing).
4. **Data-driven.** Effect, particle and sequence definitions are content: authored by shard owners, shipped through the pack or CDN pipeline (pack-format.md), referenced by id in packets. No code changes are needed for new effects.
5. **Budgets, not surprises.** Each feature has a cost budget measured with FrameStats. Particle counts and light counts scale down automatically to hold the frame budget, and the Classic preset costs nothing.
6. **Portable first.** Each feature gets an implementation that needs no compute shaders (instanced sprites), so it also works on the future WebGPU and mobile profiles. Compute shaders are an optimisation layered on top.

## Server-driven effects (protocol)

- **Today:**
  - ModernUO already supports **particle effects**: `Effects.SendLocationParticles`/`SendMovingParticles` send packet `0xC7` with a particle effect id (`OutgoingEffectPackets.cs`). The server sends `0xC7` only to clients that declare particle support (UOTD or Enhanced client flags, `Effects.ParticleSupportType`). 69 script files use these calls.
  - ClassicUO handles `0xC7` as a plain 2D graphic effect (PacketHandlers.cs:273), and its `ParticleEffect3D` handler is an empty stub.
  - Existing atmosphere packets cover personal and global light (0x4E/0x4F), weather (0x65) and screen effects (the 0x70 screen-fade types).
- **Step 1, no protocol change:**
  - declare particle support in our client;
  - map the existing particle effect ids to our particle definitions;
  - fall back to today's 2D effect when the player has particles off or an id has no definition.

  That makes existing ModernUO spell content use the new system immediately.
- **Step 2, our protocol** (the lean delta protocol in architecture-recommendations.md): new messages, negotiated through a client capability handshake:
  - **PlayEffect** `(effectId, source, target, attach mode, duration, params, classic fallback)`: attach to a mobile, item or location; follow or fixed; a seed for determinism.
  - **PlaySequence** `(sequenceId, actors, params)`: a timeline of steps (effects, sounds, light changes, camera actions), defined as content.
  - **Atmosphere** `(region or global)`: ambient light colour and level, colour grading, fog or haze, wind (drives particles and foliage), time of day (sun direction for shadows).
  - **Camera** `(shake, flash, fade, tint)`: each one individually toggleable by the player, since shake and flash affect accessibility.
  - **StopEffect** `(instanceId)`.
- **Shard control:**
  - a server setting for the recommended preset, which the client offers on first login and never forces;
  - content versioning, so effect definitions arrive with the pack update before they're used.

## Phases

| Phase | Feature | Implementation notes | Depends on |
|---|---|---|---|
| **V0** | Foundations | Feature registry plus settings UI (presets, per-feature toggles); FrameStats budgets per feature; effect, particle and sequence definition format in the pack; protocol capability flags | Pack format; protocol work |
| **V1** | **Particle system** | Particles are instanced sprites in Uo.Render (additive or alpha blend, soft depth against the world), simulated on the CPU in a worker. Emitters attach to ECS entities. `0xC7` particle ids map to definitions (step 1). A compute-shader path comes later for very large counts. | V0; ECS W3 (entities own effect attachments) |
| **V2** | **Dynamic lighting** | Move the light pass from FNA into Uo.Render. Coloured point lights with radius and flicker; smooth day/night from the Atmosphere message; tiled light lists for hundreds of lights. Classic preset = today's light look. | V0 |
| **V3** | **Normal-mapped sprites (2.5D)** | The converter generates normal maps for art and land (a heuristic or offline tool, stored as optional pack pages). The shader lights sprites per pixel with V2's lights. | V2; a converter and pack extension |
| **V4** | **Shadows** | Sun shadows from the time-of-day sun direction (an extension of today's sprite shadows), soft edges; optional light-cast shadows near the player | V2 |
| **V5** | **Post-processing** | Bloom and glow for spells, colour grading per region (Atmosphere), heat haze around fire, anti-aliasing, screen flash and fade from Camera messages | V0 |
| **V6** | **Water, terrain and weather v2** | Animated water (UV distortion, optional reflections), wind-driven foliage sway, GPU rain and snow on the V1 particles, server weather extended through Atmosphere | V1, V5 |
| **V7** | **Scaling and HiDPI** | Modern pixel-art upscaling options, explored then (the old xBR shader is dead code and stays out). Crisp integer and fractional zoom | — |

Not in scope here: UI visuals (gumps, text). They stay on FNA's UI path until graphics-ownership Tier 2 is revisited.

## Testing

- **Per feature:** screenshot goldens in both Classic and Enhanced presets. The Classic preset must match the Legacy goldens.
- **Per-feature FrameStats budgets:** the owner's FPS test gains an "Enhanced" spot set. A particle stress scene (a mass spell battle) sits beside the planned large-scale test (about 2,000 mobiles and 20,000 items).
- **Protocol:** each new message gets a unit-tested codec, a capability-negotiation test (an old server means classic behaviour), and a fallback test (feature off means the classic effect is shown).

## Open questions for the owner

- The default preset for new players (Classic or Enhanced), and whether shards can recommend one.
- Whether camera effects (shake, flash) are allowed at all, or limited to opt-in.
- The authoring workflow for shard owners: a definition format (JSON in the pack source, compiled by the converter) and whether an in-client effect preview or editor tool is wanted.
