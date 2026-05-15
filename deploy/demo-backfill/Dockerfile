# Init image for the docker-compose demo: generates and ingests 7 days of
# synthetic LLM-usage telemetry into Prometheus the first time the stack starts.
# Runs once, exits, and stays out of the way of any real polling.
FROM python:3.12-alpine

ARG PROMETHEUS_VERSION=3.0.1
ARG TARGETARCH=amd64

RUN apk add --no-cache curl tar \
 && curl -fsSL "https://github.com/prometheus/prometheus/releases/download/v${PROMETHEUS_VERSION}/prometheus-${PROMETHEUS_VERSION}.linux-${TARGETARCH}.tar.gz" \
      | tar -xz -C /tmp \
 && mv /tmp/prometheus-${PROMETHEUS_VERSION}.linux-${TARGETARCH}/promtool /usr/local/bin/promtool \
 && rm -rf /tmp/prometheus-* \
 && apk del curl tar

COPY generate.py /usr/local/bin/generate.py
COPY run.sh      /usr/local/bin/run.sh
RUN chmod +x /usr/local/bin/run.sh /usr/local/bin/generate.py

ENTRYPOINT ["/usr/local/bin/run.sh"]
