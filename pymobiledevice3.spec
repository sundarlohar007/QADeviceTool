# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['D:\\OpenCode\\QAQC\\QADeviceTool\\tools\\pymobiledevice3\\entrypoint.py'],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=['pymobiledevice3.cli.cli', 'pymobiledevice3.usbmux', 'pymobiledevice3.lockdown', 'pymobiledevice3.afc', 'pymobiledevice3.apps', 'pymobiledevice3.syslog', 'pymobiledevice3.crash', 'pymobiledevice3.diagnostics', 'pymobiledevice3.developer', 'pymobiledevice3.screenshot', 'pymobiledevice3.notification'],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='pymobiledevice3',
    debug=False,
    bootloader_ignore_signals=False,
    strip=True,
    upx=True,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=True,
    upx=True,
    upx_exclude=[],
    name='pymobiledevice3',
)
