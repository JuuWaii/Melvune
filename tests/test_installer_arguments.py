"""MSI command-line property quoting regression; never executes an installer."""
import json
import pathlib
import subprocess
import unittest


class InstallerArgumentsTests(unittest.TestCase):
    def test_msi_properties_quote_the_value_instead_of_the_whole_token(self):
        helper = (pathlib.Path(__file__).absolute().parents[1] / 'scripts/installer-arguments.ps1').as_posix()
        script = f". '{helper}'; ConvertTo-MelvuneInstallerArguments -Items @('/i','C:\\测试 空格 [1]\\app.msi','/qn','INSTALLFOLDER=C:\\测试 空格 [1]\\App','/l*v','C:\\log 空格\\install.log') | ConvertTo-Json -Compress"
        result = subprocess.run(['pwsh', '-NoProfile', '-Command', script], text=True, encoding='utf-8', capture_output=True, check=True)
        command = json.loads(result.stdout)
        self.assertIn('INSTALLFOLDER="C:\\测试 空格 [1]\\App"', command)
        self.assertNotIn('"INSTALLFOLDER=', command)
        self.assertIn('/i "C:\\测试 空格 [1]\\app.msi" /qn', command)
        self.assertIn('/l*v "C:\\log 空格\\install.log"', command)

    def test_embedded_quotes_are_rejected(self):
        helper = (pathlib.Path(__file__).absolute().parents[1] / 'scripts/installer-arguments.ps1').as_posix()
        script = f"$ErrorActionPreference='Stop'; . '{helper}'; ConvertTo-MelvuneInstallerArguments -Items @('INSTALLFOLDER=bad\"value')"
        result = subprocess.run(['pwsh', '-NoProfile', '-Command', script], text=True, encoding='utf-8', capture_output=True)
        self.assertNotEqual(result.returncode, 0)


if __name__ == '__main__':
    unittest.main()
