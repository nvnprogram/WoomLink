# WoomLink

A tool for converting Splatoon 2 XLink binary files (ELink/SLink) to a human readable text format and back.

Based on [WoomLink](https://github.com/shadowninja108/WoomLink) by **shadowninja108** -- a WIP reproduction of Nintendo's XLink library. This fork adds a full text round-trip converter (binary to text to binary) with a clean text format suitable for diffing and editing.

## Building

```
dotnet build WoomLink -c "Release (Blitz)"
```

## Usage

### Convert binary to text

```
WoomLink convert <input.belnk|bslnk> [--output <file.txt>] [--actors <ActorDB.yaml>]
```

- `--output` sets the output text file path (defaults to stdout).
- `--actors` provides an ActorDB YAML file to resolve user hashes to readable actor names in the output.

Example:

```
WoomLink convert ELink2DB.belnk --output ELink2DB.txt --actors ActorDB.yaml
```

### Rebuild text to binary

```
WoomLink rebuild <input.txt> [--output <file.belnk|bslnk>]
```

- `--output` sets the output binary file path (defaults to input path with `.bin` extension).

Example:

```
WoomLink rebuild ELink2DB.txt --output ELink2DB.belnk
```

### Legacy mode

Runs the original WoomLink print logic for a specific user or all users:

```
WoomLink legacy <elink-file> <slink-file> [--user <name>]
```

## Credits

- [shadowninja108](https://github.com/shadowninja108/WoomLink) for the original WoomLink library and XLink reverse engineering.
