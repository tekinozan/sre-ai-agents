# Kubernetes manifests

This directory contains the checked-in shared-development render of the Helm
chart. The chart in `deploy/helm/sre-ai-agent` is the source of truth; do not
hand-edit `generated-development.yaml`.

```bash
HELM_BIN=helm bash eng/render-k8s.sh
HELM_BIN=helm bash eng/check-k8s-render.sh
```

The render uses `values.development.yaml`. It uses real authentication from an
externally managed Secret and never enables the local development-auth handler.
Production uses the two-phase migration and promotion procedure in the chart
README. Secret values are intentionally absent from this directory.