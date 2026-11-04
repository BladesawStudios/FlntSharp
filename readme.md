# FlntSharp

Reader for the `.flnt` foliage placement files of Ocarina of Time on Switch 2.

```csharp
byte[] data = File.ReadAllBytes(path);
switch (FoliagePlacement.GetKind(path))
{
    case FoliageFileKind.Tree:
        FoliageTreeFile tree = FoliageTreeFile.Load(data);
        break;
    case FoliageFileKind.Undergrowth:
        FoliageUndergrowthFile undergrowth = FoliageUndergrowthFile.Load(data);
        break;
}
```

## Build

```
dotnet build FlntSharp.slnx
```

## Licence

AGPL-3.0-or-later
