"""An example mutant list: MUTANTS=example_mutants python3 scripts/mutation/run.py <worktree> results.json"""
STORE = "backend/src/ShopVerse.Infrastructure/Documents/LocalObjectStorage.cs"
M = [
    # (name, file, exact old text (must occur once), new text)
    ("store-overwrites", STORE, "File.Move(temp, path, overwrite: false);", "File.Move(temp, path, overwrite: true);"),
    ("store-key-absolute", STORE, "|| key.StartsWith('/')", ""),
]
