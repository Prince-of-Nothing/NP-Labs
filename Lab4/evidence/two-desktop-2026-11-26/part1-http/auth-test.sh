#!/bin/bash
B=http://192.168.1.41:8080
{
echo '### no token -> expect 401'
curl -sS -i -X POST -H 'Content-Type: application/json' -d '{"sender":"x","body":"x"}' "$B/topics/grades/messages"
echo
echo '### tok-phone to news -> expect 403'
curl -sS -i -X POST -H 'Authorization: Bearer tok-phone' -H 'Content-Type: application/json' -d '{"sender":"x","body":"x"}' "$B/topics/news/messages"
echo
echo '### tok-phone to grades -> expect 202'
curl -sS -i -X POST -H 'Authorization: Bearer tok-phone' -H 'Content-Type: application/json' -d '{"sender":"x","body":"auth ok"}' "$B/topics/grades/messages"
echo
} > auth.txt 2>&1
