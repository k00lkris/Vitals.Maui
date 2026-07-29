@echo off
echo Deploying Vitals API to DigitalOcean...
scp D:\GitHub\Vitals.Maui\API\main.py root@206.189.207.242:/var/www/vitals/main.py
ssh root@206.189.207.242 "systemctl restart vitals_api"
echo Vitals API deployed successfully.
pause