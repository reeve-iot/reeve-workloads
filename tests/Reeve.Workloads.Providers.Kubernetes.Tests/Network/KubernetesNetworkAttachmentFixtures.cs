namespace Reeve.Workloads.Providers.Kubernetes.Tests.Network;

internal static class KubernetesNetworkAttachmentFixtures
{
    public const string IotMacvlanUnsuppressed = """
        {
          "cniVersion": "0.3.1",
          "name": "iot-macvlan",
          "type": "macvlan",
          "master": "eth0.100",
          "mode": "bridge",
          "ipam": { "type": "dhcp" }
        }
        """;

    public const string IotMacvlanSuppressedByFlushGateway = """
        {
          "cniVersion": "0.3.1",
          "name": "iot-macvlan",
          "plugins": [
            {
              "type": "macvlan",
              "master": "eth0.100",
              "mode": "bridge",
              "ipam": { "type": "dhcp" }
            },
            {
              "type": "route-override",
              "flushgateway": true
            }
          ]
        }
        """;

    public const string IotMacvlanSuppressedByDelRoutes = """
        {
          "cniVersion": "0.3.1",
          "name": "iot-macvlan",
          "plugins": [
            {
              "type": "macvlan",
              "master": "eth0.100",
              "mode": "bridge",
              "ipam": { "type": "dhcp" }
            },
            {
              "type": "route-override",
              "delroutes": [ { "dst": "0.0.0.0/0" }, { "dst": "::/0" } ]
            }
          ]
        }
        """;

    public const string IotMacvlanSuppressedByUnknownlyNamedPlugin = """
        {
          "cniVersion": "0.3.1",
          "name": "iot-macvlan",
          "plugins": [
            {
              "type": "macvlan",
              "master": "eth0.100",
              "mode": "bridge",
              "ipam": { "type": "dhcp" }
            },
            {
              "type": "acme-defaultroute-scrubber-v4",
              "flushgateway": true
            }
          ]
        }
        """;

    public const string ExplicitDefaultRouteV4 = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-macvlan",
          "type": "macvlan",
          "master": "eth0.100",
          "ipam": {
            "type": "static",
            "addresses": [ { "address": "198.51.100.42/24" } ],
            "routes": [ { "dst": "0.0.0.0/0", "gw": "198.51.100.1" } ]
          }
        }
        """;

    public const string ExplicitDefaultRouteV6 = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-macvlan-v6",
          "type": "macvlan",
          "master": "eth0.100",
          "ipam": {
            "type": "static",
            "addresses": [ { "address": "fd30::42/64" } ],
            "routes": [ { "dst": "::/0", "gw": "fd30::1" } ]
          }
        }
        """;

    public const string StaticIpamWithGateway = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-macvlan-gw",
          "type": "macvlan",
          "master": "eth0.100",
          "ipam": {
            "type": "host-local",
            "subnet": "198.51.100.0/24",
            "gateway": "198.51.100.1"
          }
        }
        """;

    public const string GatewaylessStaticIpamWithExplicitDefaultRoute = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-both-clauses",
          "type": "macvlan",
          "master": "eth0.100",
          "ipam": {
            "type": "static",
            "addresses": [ { "address": "198.51.100.42/24" } ],
            "routes": [ { "dst": "0.0.0.0/0" } ]
          }
        }
        """;

    public const string V6GatewayWithV4OnlySuppression = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-v6-gw",
          "plugins": [
            {
              "type": "macvlan",
              "master": "eth0.100",
              "ipam": {
                "type": "static",
                "addresses": [ { "address": "fd30::42/64" } ],
                "gateway": "fd30::1"
              }
            },
            {
              "type": "route-override",
              "delroutes": [ { "dst": "0.0.0.0/0" } ]
            }
          ]
        }
        """;

    public const string GatewaylessStaticIpam = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-static",
          "type": "macvlan",
          "master": "eth0.100",
          "ipam": {
            "type": "static",
            "addresses": [ { "address": "198.51.100.42/24" } ]
          }
        }
        """;

    public const string GatewaylessWhereaboutsIpam = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-whereabouts",
          "type": "ipvlan",
          "master": "eth0.100",
          "ipam": {
            "type": "whereabouts",
            "range": "198.51.100.0/24",
            "gateway": ""
          }
        }
        """;

    public const string NoGatewayNoDynamicIpam = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-hostlocal",
          "type": "macvlan",
          "master": "eth0.100",
          "ipam": {
            "type": "host-local",
            "ranges": [ [ { "subnet": "198.51.100.0/24" } ] ],
            "routes": [ { "dst": "10.0.0.0/8" } ]
          }
        }
        """;

    public const string UnrecognisedChainedPlugin = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-unknown-chain",
          "plugins": [
            {
              "type": "macvlan",
              "master": "eth0.100",
              "ipam": {
                "type": "static",
                "addresses": [ { "address": "198.51.100.42/24" } ]
              }
            },
            {
              "type": "acme-policy-router",
              "table": 200
            }
          ]
        }
        """;

    public const string UnrecognisedChainedPluginRenamed = """
        {
          "cniVersion": "0.3.1",
          "name": "lab-unknown-chain",
          "plugins": [
            {
              "type": "macvlan",
              "master": "eth0.100",
              "ipam": {
                "type": "static",
                "addresses": [ { "address": "198.51.100.42/24" } ]
              }
            },
            {
              "type": "globex-policy-router",
              "table": 200
            }
          ]
        }
        """;

    public const string Unparseable = """{ "cniVersion": "0.3.1", "type": "macvlan",,, """;

    public const string Empty = "   ";
}
