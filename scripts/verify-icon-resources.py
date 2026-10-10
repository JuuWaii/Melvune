"""Check the actual Windows PE icon images against the selected ICO artwork."""
import argparse
import pathlib
import struct

def ico_images(file):
    data = file.read_bytes()
    reserved, kind, count = struct.unpack_from('<HHH', data)
    assert (reserved, kind) == (0, 1)
    result = {}
    for i in range(count):
        width, height, _, _, _, _, size, offset = struct.unpack_from('<BBBBHHII', data, 6 + i * 16)
        result[(width or 256, height or 256)] = data[offset:offset + size]
    return result

def pe_icons(file):
    data = file.read_bytes()
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    assert data[pe:pe + 4] == b'PE\0\0'
    count = struct.unpack_from('<H', data, pe + 6)[0]
    optional_size = struct.unpack_from('<H', data, pe + 20)[0]
    optional = pe + 24
    directory = optional + (112 if struct.unpack_from('<H', data, optional)[0] == 0x20b else 96)
    resource_rva = struct.unpack_from('<I', data, directory + 16)[0]
    def offset(rva):
        for i in range(count):
            section = optional + optional_size + 40 * i
            virtual_size, virtual_address, raw_size, raw_address = struct.unpack_from('<IIII', data, section + 8)
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_address + rva - virtual_address
        raise ValueError('Resource RVA outside sections')
    base = offset(resource_rva)
    resources = {}
    def walk(relative, parts):
        named, ids = struct.unpack_from('<HH', data, base + relative + 12)
        for i in range(named + ids):
            name, target = struct.unpack_from('<II', data, base + relative + 16 + i * 8)
            if target & 0x80000000: walk(target & 0x7fffffff, parts + (name,))
            else:
                rva, size = struct.unpack_from('<II', data, base + target)
                resources[parts + (name,)] = data[offset(rva):offset(rva) + size]
    walk(0, ())
    images = {key[1]: value for key, value in resources.items() if key[0] == 3}
    groups = []
    for key, value in resources.items():
        if key[0] != 14: continue
        result = {}
        for i in range(struct.unpack_from('<H', value, 4)[0]):
            w, h, _, _, _, _, _, resource_id = struct.unpack_from('<BBBBHHIH', value, 6 + i * 14)
            result[(w or 256, h or 256)] = images[resource_id]
        groups.append(result)
    return groups

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--icon', type=pathlib.Path, required=True)
    parser.add_argument('executables', nargs='+', type=pathlib.Path)
    args = parser.parse_args()
    expected = ico_images(args.icon)
    assert set(expected) == {(s, s) for s in [16, 24, 32, 48, 64, 128, 256]}
    for file in args.executables:
        assert any(group == expected for group in pe_icons(file)), 'Selected artwork is missing from ' + file.name
        print(file.name + ': selected icon matches all seven sizes')
