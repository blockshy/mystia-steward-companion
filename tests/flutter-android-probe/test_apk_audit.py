import importlib.util
import pathlib
import struct
import unittest

spec = importlib.util.spec_from_file_location('apk_audit', pathlib.Path(__file__).parents[2] / 'scripts/audit-flutter-android-apk.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def elf64(alignment=16384, offset=0, vaddr=0, relro_end=16384, extra_writable=0):
    ident = b'\x7fELF' + bytes([2, 1, 1]) + bytes(9)
    header = struct.pack('<HHIQQQIHHHHHH', 3, 183, 1, 0, 64, 0, 0, 64, 56, 2, 0, 0, 0)
    load = struct.pack('<IIQQQQQQ', 1, 6, offset, vaddr, 0, 128, relro_end + extra_writable, alignment)
    relro = struct.pack('<IIQQQQQQ', 0x6474E552, 4, 0, 0, 0, 128, relro_end, 1)
    return ident + header + load + relro


class AlignmentFailureTests(unittest.TestCase):
    def test_64_bit_valid_alignment(self):
        self.assertTrue(audit.audit_elf(elf64(), 16384)['passed'])

    def test_4k_load_is_not_16k_compatible(self):
        self.assertFalse(audit.audit_elf(elf64(alignment=4096), 16384)['passed'])

    def test_incongruent_virtual_and_file_offsets_rejected(self):
        self.assertFalse(audit.audit_elf(elf64(offset=4096), 16384)['passed'])

    def test_relro_rounding_must_not_protect_other_writable_bytes(self):
        self.assertFalse(audit.audit_elf(elf64(relro_end=4096, extra_writable=4096), 16384)['passed'])

    def test_relro_rounding_into_padding_is_safe(self):
        result = audit.audit_elf(elf64(relro_end=4096), 16384)
        self.assertTrue(result['passed'])
        self.assertFalse(result['relroSegments'][0]['endAligned'])

    def test_non_power_of_two_load_alignment_rejected(self):
        self.assertFalse(audit.audit_elf(elf64(alignment=24576), 16384)['passed'])

    def test_truncated_elf_is_not_accepted(self):
        with self.assertRaises((ValueError, struct.error)):
            audit.audit_elf(b'\x7fELF', 16384)


if __name__ == '__main__':
    unittest.main()
