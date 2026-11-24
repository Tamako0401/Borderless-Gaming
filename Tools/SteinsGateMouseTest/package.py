"""Package the Win32 Release build of the Steins;Gate mouse test."""
import argparse
from pathlib import Path, PurePosixPath
import subprocess
from zipfile import ZIP_DEFLATED, ZipFile

ROOT = Path(__file__).resolve().parents[2]
APP_RELEASE = ROOT / "BorderlessGaming" / "bin" / "Release"
FOLDER = PurePosixPath("Borderless-Gaming-SteinsGate-Mouse-Test-v2")


def create_package(native_build: Path, output: Path) -> None:
    revision = subprocess.check_output(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
    ).strip()
    readme = (Path(__file__).parent / "START-HERE.txt").read_text(encoding="utf-8")
    readme += f"\nSource commit\nhttps://github.com/Tamako0401/Borderless-Gaming/commit/{revision}\n"
    output.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(output, "w", ZIP_DEFLATED, compresslevel=9) as archive:
        archive.write(APP_RELEASE / "BorderlessGaming.exe", FOLDER / "Start Mouse Test.exe")
        archive.write(
            APP_RELEASE / "BorderlessGaming.exe.config", FOLDER / "Start Mouse Test.exe.config"
        )
        for dll in sorted(APP_RELEASE.rglob("*.dll")):
            archive.write(dll, FOLDER / dll.relative_to(APP_RELEASE).as_posix())
        archive.write(
            native_build / "Release" / "SteinsGateInputFix.dll", FOLDER / "SteinsGateInputFix.dll"
        )
        for language in sorted((ROOT / "Languages").glob("*.lang")):
            archive.write(language, FOLDER / "test-data" / "Languages" / language.name)
        archive.write(ROOT / "LICENSE", FOLDER / "LICENSE.txt")
        archive.writestr((FOLDER / "steins-gate-test.txt").as_posix(), b"")
        archive.writestr((FOLDER / "START-HERE.txt").as_posix(), readme.encode("utf-8-sig"))

    with ZipFile(output) as archive:
        if archive.testzip() is not None:
            raise RuntimeError("The ZIP failed its integrity check")
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-build", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    create_package(args.native_build, args.output)
