#!/bin/bash
B=192.168.1.41:5000
P=/home/cup/bin/lab4-grpc-publisher
{
echo '### no token -> expect Unauthenticated'
echo x | "$P" "$B" grades x
echo
echo '### tok-phone to news -> expect PermissionDenied'
echo x | "$P" "$B" news x tok-phone
echo
echo '### tok-phone to grades -> expect OK'
echo 'auth ok' | "$P" "$B" grades x tok-phone
echo
} > auth.txt 2>&1
