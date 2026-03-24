#!/usr/bin/env bash

docker build -t scrapbot:latest .
docker volume create scrapbot-data || true
docker run -d -v scrapbot-data:/app/data --restart unless-stopped --name scrapbot_container scrapbot:latest
