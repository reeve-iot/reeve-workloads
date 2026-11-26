namespace Reeve.Workloads.Core.Tests.Rendering;

internal static class ArtifactGoldens
{
    internal const string EnvExternalSecret =
        """
        # Reeve workload environment — reeve-wl-com-reeve-matter-matter-11111111
        # One KEY=value per line. Deploy the sidecar container with this environment.
        LOG_LEVEL=info
        REEVE_GATEWAY_URL=wss://reeve.example.local/workloads/gw
        REEVE_SPEC_HASH=abc123def456
        REEVE_WORKLOAD_TOKEN=<set-this-secret>
        """;

    internal const string EnvRevealedToken =
        """
        # Reeve workload environment — reeve-wl-com-reeve-matter-matter-11111111
        # One KEY=value per line. Deploy the sidecar container with this environment.
        LOG_LEVEL=info
        REEVE_GATEWAY_URL=wss://reeve.example.local/workloads/gw
        REEVE_SPEC_HASH=abc123def456
        REEVE_WORKLOAD_TOKEN=rwt_goldentoken
        """;

    internal const string ComposeExternalSecret =
        """
        # Reeve workload compose file — reeve-wl-com-reeve-matter-matter-11111111
        # docker compose up -d
        services:
          reeve-wl-com-reeve-matter-matter-11111111:
            image: "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111"
            restart: unless-stopped
            environment:
              "LOG_LEVEL": "info"
              "REEVE_GATEWAY_URL": "wss://reeve.example.local/workloads/gw"
              "REEVE_SPEC_HASH": "abc123def456"
              "REEVE_WORKLOAD_TOKEN": "<set-this-secret>"
            ports:
              - "18080:8080/tcp"
            cap_drop:
              - ALL
            cap_add:
              - NET_ADMIN
            devices:
              - "/dev/net/tun:/dev/net/tun"
              - "/dev/ttyUSB0:/dev/ttyUSB0"
            volumes:
              - "reeve-com-reeve-matter-data-11111111:/data"
              - "/var/run/dbus:/var/run/dbus:ro"
        volumes:
          reeve-com-reeve-matter-data-11111111:
            name: "reeve-com-reeve-matter-data-11111111"
        """;

    internal const string ComposeRevealedToken =
        """
        # Reeve workload compose file — reeve-wl-com-reeve-matter-matter-11111111
        # docker compose up -d
        services:
          reeve-wl-com-reeve-matter-matter-11111111:
            image: "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111"
            restart: unless-stopped
            environment:
              "LOG_LEVEL": "info"
              "REEVE_GATEWAY_URL": "wss://reeve.example.local/workloads/gw"
              "REEVE_SPEC_HASH": "abc123def456"
              "REEVE_WORKLOAD_TOKEN": "rwt_goldentoken"
            cap_drop:
              - ALL
        """;

    internal const string ComposeHostNetworkNodeLocalState =
        """
        # Reeve workload compose file — reeve-wl-com-reeve-matter-matter-11111111
        # docker compose up -d
        services:
          reeve-wl-com-reeve-matter-matter-11111111:
            image: "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111"
            restart: unless-stopped
            network_mode: host
            environment:
              "LOG_LEVEL": "info"
              "REEVE_GATEWAY_URL": "wss://reeve.example.local/workloads/gw"
              "REEVE_SPEC_HASH": "abc123def456"
              "REEVE_WORKLOAD_TOKEN": "<set-this-secret>"
            cap_drop:
              - ALL
            volumes:
              - "reeve-com-reeve-matter-data-11111111:/data"
              - "/var/lib/reeve/ble/state:/var/lib/reeve/ble/state"
              - "/var/run/dbus:/var/run/dbus:ro"
        volumes:
          reeve-com-reeve-matter-data-11111111:
            name: "reeve-com-reeve-matter-data-11111111"
        """;

    internal const string ComposeHostNetworkWithHostPortOmitsPorts =
        """
        # Reeve workload compose file — reeve-wl-com-reeve-matter-matter-11111111
        # docker compose up -d
        services:
          reeve-wl-com-reeve-matter-matter-11111111:
            image: "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111"
            restart: unless-stopped
            network_mode: host
            environment:
              "LOG_LEVEL": "info"
              "REEVE_GATEWAY_URL": "wss://reeve.example.local/workloads/gw"
              "REEVE_SPEC_HASH": "abc123def456"
              "REEVE_WORKLOAD_TOKEN": "<set-this-secret>"
            cap_drop:
              - ALL
        """;

    internal const string HelmValuesExternalSecret =
        """
        # Reeve workload Helm values — reeve-wl-com-reeve-matter-matter-11111111
        # Feed this to a generic workload chart: helm install <name> <chart> -f values.yaml
        image:
          reference: "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111"
        env:
          - name: "LOG_LEVEL"
            value: "info"
          - name: "REEVE_GATEWAY_URL"
            value: "wss://reeve.example.local/workloads/gw"
          - name: "REEVE_SPEC_HASH"
            value: "abc123def456"
          - name: "REEVE_WORKLOAD_TOKEN"
            value: "<set-this-secret>"
        resources:
          requests:
            cpu: "100m"
            memory: "256Mi"
          limits:
            memory: "512Mi"
        ports:
          - name: "api"
            containerPort: 8080
            protocol: TCP
        persistence:
          - id: data
            mountPath: /data
            size: "1Gi"
            criticality: critical
            accessMode: rwo
        securityContext:
          privileged: false
          runAsNonRoot: true
          capabilities:
            add:
              - NET_ADMIN
        hostDevices:
          - /dev/ttyUSB0
        """;

    internal const string HelmValuesFieldComplete =
        """
        # Reeve workload Helm values — reeve-wl-com-reeve-matter-matter-11111111
        # Feed this to a generic workload chart: helm install <name> <chart> -f values.yaml
        image:
          reference: "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111"
        env:
          - name: "LOG_LEVEL"
            value: "info"
          - name: "REEVE_GATEWAY_URL"
            value: "wss://reeve.example.local/workloads/gw"
          - name: "REEVE_SPEC_HASH"
            value: "abc123def456"
          - name: "REEVE_WORKLOAD_TOKEN"
            value: "<set-this-secret>"
        hostNetwork: true
        ports:
          - name: "api"
            containerPort: 8080
            protocol: TCP
        metrics:
          port: 9090
          path: /metrics
        configMaps:
          - name: "reeve-matter-config"
            mountPath: /etc/reeve/config
            readOnly: true
        securityContext:
          privileged: false
          runAsNonRoot: true
          capabilities:
            add:
              - NET_ADMIN
        nodeLocalState:
          hostPath: /var/lib/reeve/ble/state
          criticality: critical
        """;

    internal const string HelmValuesAdoptedClaimRestore =
        """
        # Reeve workload Helm values — reeve-wl-com-reeve-matter-matter-11111111
        # Feed this to a generic workload chart: helm install <name> <chart> -f values.yaml
        image:
          reference: "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111"
        env:
          - name: "LOG_LEVEL"
            value: "info"
          - name: "REEVE_GATEWAY_URL"
            value: "wss://reeve.example.local/workloads/gw"
          - name: "REEVE_SPEC_HASH"
            value: "abc123def456"
          - name: "REEVE_WORKLOAD_TOKEN"
            value: "<set-this-secret>"
        persistence:
          - id: cache
            mountPath: /cache
            size: "1Gi"
            criticality: recoverable
            accessMode: rwo
          - id: state
            mountPath: /state
            existingClaim: reeve-wl-pvc-retained-state
            criticality: critical
            accessMode: rwo
        """;

    internal const string KubernetesManifest =
        """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          labels:
            reeve.io/workload-instance: 11111111-2222-3333-4444-555555555555
            reeve.io/installation: install-1
            reeve.io/spec-hash: abc123def456
          name: reeve-wl-com-reeve-matter-matter-11111111
          namespace: reeve
        spec:
          replicas: 1
          selector:
            matchLabels:
              reeve.io/workload-instance: 11111111-2222-3333-4444-555555555555
          strategy:
            type: Recreate
          template:
            metadata:
              annotations:
                reeve.io/spec-hash: abc123def456
              labels:
                reeve.io/workload-instance: 11111111-2222-3333-4444-555555555555
                reeve.io/installation: install-1
                reeve.io/spec-hash: abc123def456
            spec:
              containers:
              - env:
                - name: REEVE_WORKLOAD_TOKEN
                  valueFrom:
                    secretKeyRef:
                      key: REEVE_WORKLOAD_TOKEN
                      name: reeve-wl-com-reeve-matter-matter-11111111
                - name: REEVE_GATEWAY_URL
                  value: wss://reeve.example.local/workloads/gw
                - name: LOG_LEVEL
                  value: info
                - name: REEVE_SPEC_HASH
                  value: abc123def456
                image: ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111
                name: matter
                securityContext:
                  allowPrivilegeEscalation: false
                  capabilities:
                    drop:
                    - ALL
                  runAsNonRoot: true
                  seccompProfile:
                    type: RuntimeDefault
        """;

    internal const string KubernetesManifestWithPodPatch =
        """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          labels:
            reeve.io/workload-instance: 11111111-2222-3333-4444-555555555555
            reeve.io/installation: install-1
            reeve.io/spec-hash: abc123def456
          name: reeve-wl-com-reeve-matter-matter-11111111
          namespace: reeve
        spec:
          replicas: 1
          selector:
            matchLabels:
              reeve.io/workload-instance: 11111111-2222-3333-4444-555555555555
          strategy:
            type: Recreate
          template:
            metadata:
              annotations:
                reeve.io/spec-hash: abc123def456
              labels:
                reeve.io/workload-instance: 11111111-2222-3333-4444-555555555555
                reeve.io/installation: install-1
                reeve.io/spec-hash: abc123def456
            spec:
              containers:
              - env:
                - name: REEVE_WORKLOAD_TOKEN
                  valueFrom:
                    secretKeyRef:
                      key: REEVE_WORKLOAD_TOKEN
                      name: reeve-wl-com-reeve-matter-matter-11111111
                - name: REEVE_GATEWAY_URL
                  value: wss://reeve.example.local/workloads/gw
                - name: LOG_LEVEL
                  value: info
                - name: REEVE_SPEC_HASH
                  value: abc123def456
                image: ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111
                name: matter
                securityContext:
                  allowPrivilegeEscalation: false
                  capabilities:
                    drop:
                    - ALL
                  runAsNonRoot: true
                  seccompProfile:
                    type: RuntimeDefault
              hostname: matter-sidecar
        """;
}
