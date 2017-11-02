# NetSplitter

NetSplitter is a small tool to duplicate a network stream to another computer. It acts as a proxy that forward a stream to a main target while duplicating the same stream to other secondary targets.

It can be used in several day-to-day scenarii:
- Simulate load on a web server, by duplicating a single stream on the same target multiple times
- Duplicate production load on a development machine in order to easily debug in production environment
- Duplicate a production network stream on a backup server

It can be used as a L4 TCP proxy, or as a L7 HTTP reverse proxy. The HTTP layer will handle **Host** add **X-Forwarded-For** headers.

NetSplitter aims to directly forward a stream to the main target. Only the main target can respond to clients.
A copy of each received buffer will be stored in secondary targets buffer, until sent. If a secondary target does not process data quickly enough or does not respond, it will be discarded.

## Usage

NetSplitter uses an JSON configuration file to describe its behavior, automatically reloaded upon modification.

Here is a sample configuration file

```json
{
    "Settings":
    {
        "Mode": "Http", // Tcp, Http
        "Port": 80,
        "Balancing": "Random", // Random, IPHash, LeastConn
        "BufferSize": 1048576,
        "Timeout": 1000
    },
    "Balancing":
    [
        { "Hostname": "prod1", "Port": 80, "Weight": 2 },
        { "Hostname": "prod2", "Port": 80 },
        { "Hostname": "prod3", "Port": 80, "Enabled": false }
    ],
    "Cloning":
    [
        { "Hostname": "127.0.0.1", "Port": 8080 }
        { "Hostname": "dev1", "Port": 8080, "Enabled": false }
    ]
}
```

- **Settings.Port**: _(Mandatory)_ Port to listen to
- **Settings.Mode**: Mode to use to proxy requests
    - **TCP**: L4 proxy
    - **HTTP**: L7 proxy, with support for _Host_ and _X-Forwarded-For_ headers
- **Settings.Balancing**: Balancing mode to use to select the master target
    - **Random**: A random target will be selected according to specified weights
    - **IPHash**: Each client IP will have a deterministic target according to specified weights. This mode can be used to ensure that a client always connect to the same server, for HTTP website for examples
    - **LeastConn**: The target with the least number of connection will get the new one, according to specified weights
- **Settings.BufferSize**: Maximum buffer size to use per connection
- **Settings.Timeout**: Timeout in ms after which secondary targets will be discarded
- **Settings.Target**: Description of the main target to communicate to
- **Balancing.***: List of master hosts to connect to
- **Cloning.***: List of secondary hosts
    - **.Hostname**: _(Mandatory)_ Target to connect to
    - **.Port**: _(Mandatory)_ Target port to connect to
    - **.Weight**: Weight of this target. A greater weight means it will be choosen more often. Default is 1
    - **.Enabled**: Use false to disable this target

If not specified, NetSplitter will automatically choose HTTP mode and IPHash balancing if you specify port 80 or 8080.