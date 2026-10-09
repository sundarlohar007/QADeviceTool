"""PyInstaller entrypoint: configure pipes before importing the CLI."""
import sys


def configure_output():
    for stream in (sys.stdout, sys.stderr):
        if stream is not None and hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="backslashreplace", line_buffering=True)


configure_output()

if __name__ == "__main__":
    if sys.argv[1:] == ["--logpro-encoding-check"]:
        sample = "LogPro Unicode: \u202f \U0001f4f1 \u65e5\u672c\u8a9e"
        print(sample, flush=True)
        print(sample, file=sys.stderr, flush=True)
    else:
        from pymobiledevice3.__main__ import main
        main()
