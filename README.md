# NetSplitter

NetSplitter is a small tool to duplicate a network stream to another computer. It acts as a proxy that forward a TCP stream to a main target while duplicating the same stream to other secondary targets.

It can be used in several day-to-day scenarii:
- Simulate load on a web server, by duplicating a single stream on the same target multiple times
- Duplicate production load on a development machine in order to easily debug in production environment
- Duplicate a production network stream on a backup server

NetSplitter aims to directly forward TCP stream to the main target. Only the main target can respond to clients.
A copy of each received buffer will be stored in secondary targets buffer, until sent. If a secondary target does not process data quickly enough or does not respond, it will be discarded.

## Usage

NetSplitter uses an XML configuration file to describe its behavior, automatically reloaded upon modification.

Here is a sample configuration file

```{xml}
<?xml version="1.0" encoding="utf-8" ?>
<NetSplitter>
    <Settings>
        <Port>80</Port>
        <Target Hostname="main-server" Port="80" />
    </Settings>
    <Output BufferSize="1048576" Timeout="1000">
        <Target Hostname="dev-workstation" Port="80" />
    </Output>
</NetSplitter>
```

- **Settings/Port**: Port to listen to
- **Settings/Target**: Description of the main target to communicate to
- **Output@BufferSize**: The buffer size limit per secondary target. If the buffer exceed this limit, the target will be discarded for the current connection
- **Output@Timeout**: The timeout to connect to secondary targets
- **Output/Target**: Description of all secondary targets