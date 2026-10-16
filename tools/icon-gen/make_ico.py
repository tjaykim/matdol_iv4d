import os
import struct
import sys

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

def main():
    assets = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), '..', '..', 'Assets')
    blobs = [open(os.path.join(assets, 'Icons', f'icon-{n}.png'), 'rb').read() for n in SIZES]
    head = struct.pack('<HHH', 0, 1, len(SIZES))
    off = 6 + 16 * len(SIZES)
    entries = b''
    for n, b in zip(SIZES, blobs):
        entries += struct.pack('<BBBBHHII', n % 256, n % 256, 0, 0, 1, 32, len(b), off)
        off += len(b)
    with open(os.path.join(assets, 'matdol.ico'), 'wb') as f:
        f.write(head + entries + b''.join(blobs))

if __name__ == '__main__':
    main()
