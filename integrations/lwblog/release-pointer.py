#!/usr/bin/env python3
"""Activate a fully prepared static tree using a single atomic symlink replacement."""
import argparse
import os
from pathlib import Path
import uuid


def activate(pointer: Path, target: Path):
    target = target.resolve(strict=True)
    for name in ('index.html', 'en/index.html'):
        if not (target / name).is_file():
            raise ValueError(f'Incomplete release: {name}')
    if pointer.exists() and not pointer.is_symlink():
        raise ValueError('Refusing to replace a real directory or file')
    temporary = pointer.with_name(pointer.name + '.next-' + uuid.uuid4().hex)
    try:
        temporary.symlink_to(target, target_is_directory=True)
        os.replace(temporary, pointer)
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('pointer', type=Path)
    parser.add_argument('target', type=Path)
    args = parser.parse_args()
    activate(args.pointer, args.target)
