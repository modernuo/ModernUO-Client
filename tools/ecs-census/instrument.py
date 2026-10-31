"""Rename (mode R) or make get-only (mode W) the A members of the world-object classes, to let the C# compiler list every
reference. Usage: instrument.py <repo-root> R|W   (revert with git checkout -- src)"""
import re, sys

root, mode = sys.argv[1], sys.argv[2]
G = root + "/src/ClassicUO.Client/Game/GameObjects/"

def D(name, mods="(?:public|internal)(?: virtual| override)?"):
    """Declaration of member `name` as a field or (since T0c) a property: `public T Name;`, `public T Name = x;`,
    `public T Name { get; set; }`, `public T Name { get => ...` or `public T Name` followed by a `{` on the next line."""
    return r"^[ \t]*" + mods + r" [\w<>?\[\],]+ (" + name + r")\b(?=[ \t]*(?:\{|=[^>=]|;|\r?$))"


# (file, declaration regex with one group = the member name). Every entry must match exactly once or the run fails.
DECLS = [("GameObject.cs", D(n)) for n in "Graphic Hue Offset OffsetX OffsetY OffsetZ X Y Z".split()]
DECLS += [("Entity.cs", D(n)) for n in "AnimIndex ExecuteAnimation LastAnimationChangeTime Flags Hits HitsMax LastStepTime Name Serial Direction".split()]
DECLS += [("Item.cs", D(n)) for n in "Amount Container IsDamageable Layer LightID Price UsedLayer DisplayedGraphic IsMulti".split()]
DECLS += [("Mobile.cs", D(n)) for n in ("AnimationFrameCount AnimationFromServer IsRenamable LastStepSoundTime IsFemale IsRunning NotorietyFlag "
                                       "IsDead InWarMode Race SpeedMode Stamina StaminaMax Mana ManaMax StepSoundOffset Title").split()]
DECLS += [("PlayerMobile.cs", D("InWarMode"))]

PLAIN = r"^(public|internal) [\w<>?]+ (\w+)( = [^;]+)?;$"  # fields that can be turned into { get; } in mode W


def main():
    texts = {}
    failed = []
    for f, rx in DECLS:
        t = texts.setdefault(f, open(G + f, encoding="utf-8-sig").read())
        ms = list(re.finditer(rx, t, re.M))
        if len(ms) != 1:
            print("NO MATCH" if not ms else "AMBIGUOUS (%d)" % len(ms), f, rx)
            failed.append((f, rx))
            continue
        m = ms[0]
        s, e = m.span(1)
        name = m.group(1)
        if mode == "R":
            t = t[:s] + "Zz_" + name + t[e:]
        else:
            # auto-property: drop the setter so every write is a compile error; other members are left as they are
            rest = t[e:]
            am = re.match(r"\s*\{\s*get;\s*set;\s*\}", rest)
            if am:
                t = t[:e] + " { get; }" + t[e + am.end():]
        texts[f] = t
    if failed:
        sys.exit("%d of %d declarations unmatched or ambiguous; nothing written (update DECLS)" % (len(failed), len(DECLS)))
    for f, t in texts.items():
        open(G + f, "w", encoding="utf-8").write(t)
    print("%d of %d declarations matched" % (len(DECLS), len(DECLS)))


main()
