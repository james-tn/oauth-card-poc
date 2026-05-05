FROM python:3.11-slim

WORKDIR /app

# Install deps first for better layer caching
COPY pyproject.toml /app/
RUN pip install --no-cache-dir \
    "botbuilder-core>=4.16.2" \
    "botbuilder-dialogs>=4.16.2" \
    "botbuilder-integration-aiohttp>=4.16.2" \
    "botbuilder-schema>=4.16.2" \
    "aiohttp>=3.9" \
    "python-dotenv>=1.0"

COPY app.py /app/

# Container Apps default ingress port
ENV BOT_PORT=8080
EXPOSE 8080

CMD ["python", "app.py"]
