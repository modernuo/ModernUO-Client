"""Rename (mode R) or make get-only (mode W) the A members of the world-object classes, to let the C# compiler list every
reference. Usage: instrument.py <repo-root> R|W   (revert with git checkout -- src)"""
import re, sys

root, mode = sys.argv[1], sys.argv[2]
G = root + "/src/ClassicUO.Client/Game/GameObjects/"

# (file, declaration regex with one group = the member name)
DECLS = [
    ("GameObject.cs", r"public ushort (Graphic);"),
    ("GameObject.cs", r"public ushort (Hue);"),
    ("GameObject.cs", r"public Vector3 (Offset);"),
    ("GameObject.cs", r"public ushort X,\n\s+(Y);"),
    ("GameObject.cs", r"public ushort (X),\n"),
    ("GameObject.cs", r"public sbyte (Z);"),
    ("Entity.cs", r"public byte (AnimIndex);"),
    ("Entity.cs", r"public bool (ExecuteAnimation) = true;"),
    ("Entity.cs", r"internal long (LastAnimationChangeTime);"),
    ("Entity.cs", r"public Flags (Flags);"),
    ("Entity.cs", r"public ushort (Hits)\n"),
    ("Entity.cs", r"public ushort (HitsMax)\n"),
    ("Entity.cs", r"public uint (LastStepTime);"),
    ("Entity.cs", r"public string (Name);"),
    ("Entity.cs", r"public uint (Serial);"),
    ("Entity.cs", r"public Direction (Direction)\n"),
    ("Item.cs", r"public ushort (Amount);"),
    ("Item.cs", r"public uint (Container) = 0xFFFF_FFFF;"),
    ("Item.cs", r"public bool (IsDamageable);"),
    ("Item.cs", r"public Layer (Layer);"),
    ("Item.cs", r"public byte (LightID);"),
    ("Item.cs", r"public uint (Price);"),
    ("Item.cs", r"public bool (UsedLayer);"),
    ("Item.cs", r"public ushort (DisplayedGraphic)\n"),
    ("Item.cs", r"public bool (IsMulti)\n"),
    ("Mobile.cs", r"public byte (AnimationFrameCount);"),
    ("Mobile.cs", r"public bool (AnimationFromServer);"),
    ("Mobile.cs", r"public bool (IsRenamable);"),
    ("Mobile.cs", r"public long (LastStepSoundTime);"),
    ("Mobile.cs", r"public bool (IsFemale)\n"),
    ("Mobile.cs", r"public bool (IsRunning)\n"),
    ("Mobile.cs", r"public NotorietyFlag (NotorietyFlag)\n"),
    ("Mobile.cs", r"public bool (IsDead)\n"),
    ("Mobile.cs", r"public virtual bool (InWarMode)\n"),
    ("PlayerMobile.cs", r"public override bool (InWarMode)\n"),
    ("Mobile.cs", r"public RaceType (Race);"),
    ("Mobile.cs", r"public CharacterSpeedType (SpeedMode) = CharacterSpeedType.Normal;"),
    ("Mobile.cs", r"public ushort (Stamina);"),
    ("Mobile.cs", r"public ushort (StaminaMax);"),
    ("Mobile.cs", r"public ushort (Mana);"),
    ("Mobile.cs", r"public ushort (ManaMax);"),
    ("Mobile.cs", r"public int (StepSoundOffset);"),
    ("Mobile.cs", r"public string (Title) = string.Empty;"),
]

PLAIN = r"^(public|internal) [\w<>?]+ (\w+)( = [^;]+)?;$"  # fields that can be turned into { get; } in mode W


def main():
    texts = {}
    for f, rx in DECLS:
        t = texts.setdefault(f, open(G + f, encoding="utf-8-sig").read())
        m = re.search(rx, t)
        if not m:
            print("NO MATCH", f, rx)
            continue
        s, e = m.span(1)
        name = m.group(1)
        is_field = ";" in m.group(0) or m.group(0).endswith(",\n")
        if mode == "R":
            t = t[:s] + "Zz_" + name + t[e:]
        else:
            if is_field:
                # public T Name[ = init]; -> public T Name { get; } [= init];  (init needs a property initializer)
                full = m.group(0)
                if full.endswith(",\n"):
                    t = t[:e] + " { get; }" + t[e:]  # 'X,' handled below
                else:
                    t = t[:e] + " { get; }" + t[e:]
            else:
                # property with accessors: make the setter private
                t = t[:s] + name + t[e:]
        texts[f] = t
    for f, t in texts.items():
        open(G + f, "w", encoding="utf-8").write(t)


main()
