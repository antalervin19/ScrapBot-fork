#!/usr/bin/env bash

docker container kill scrapbot_container
docker container remove scrapbot_container
docker build -t scrapbot:latest .
docker volume create scrapbot-data || true
docker run -d -v scrapbot-data:/app/data --restart unless-stopped --name scrapbot_container scrapbot:latest
