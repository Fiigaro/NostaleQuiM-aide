# NostaleQuiM-aide

Projet educatif autour de [NosSmooth](https://github.com/Rutherther/NosSmooth)
(bibliotheque C# / .NET sous licence MIT).

## Contenu

- [`docs/packet-handler-di.md`](docs/packet-handler-di.md) — comment l'injection
  de dependances de NosSmooth intercepte et deserialise des paquets textuels,
  et les pieges de cablage courants.
- [`samples/PacketHandlerDemo`](samples/PacketHandlerDemo) — application console
  minimale : un paquet personnalise, deux responders asynchrones, et un client
  qui rejoue un fichier de paquets (aucun jeu requis).

## Lancer l'exemple

```bash
cd samples/PacketHandlerDemo
dotnet run                       # utilise packets-sample.txt
dotnet run -- /chemin/vers/mes-paquets.txt
```
