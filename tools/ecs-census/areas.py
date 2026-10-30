import re

DEV_FILES = {"DevBench.cs", "Game/Ecs/EcsInspector.cs", "Game/Ecs/EcsSessionDriver.cs", "Game/Ecs/EcsParity.cs", "Game/Ecs/BenchEffects.cs", "Game/Scenes/PrepCheck.cs", "Game/Scenes/WorldSettleGate.cs"}


def area(path):
    """path relative to src/ClassicUO.Client/ -> (area, hot|cold|dev)"""
    p = path
    if p in DEV_FILES or p.startswith("DevBench") or "Check" in p.split("/")[-1] and p.startswith("Game/Ecs/"):
        return "dev/checkers", "dev"
    if p.startswith("Game/GameObjects/Views/"):
        return "views", "hot"
    if p.startswith("Game/Scenes/"):
        return "scenes (walk, prepare, pick)", "hot"
    if p.startswith("Game/Map/"):
        return "map/chunks", "hot"
    if p.startswith("Game/Ecs/"):
        return "ecs (mirror, extract, systems)", "hot"
    if p.startswith("Game/GameObjects/"):
        return "entity classes (update)", "hot"
    if p == "Game/World.cs":
        return "world (update, lookup)", "hot"
    if p.startswith("Network/"):
        return "network (packet handlers)", "net"
    if p.startswith("Game/UI/"):
        return "ui (gumps, controls)", "cold"
    if p.startswith("Game/Managers/") or p in ("Game/GameActions.cs", "Game/Pathfinder.cs", "Game/SelectedObject.cs", "Game/GameCursor.cs", "Game/ItemHold.cs"):
        return "managers/actions", "cold"
    return "other", "cold"
