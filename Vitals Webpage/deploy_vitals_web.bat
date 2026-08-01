@echo off
echo Deploying Vitals website to DigitalOcean...
scp -r C:\Users\krist\Documents\vitals_api\vitals_webpage\* root@206.189.207.242:/var/www/vitals/static/
echo Vitals website deployed successfully.
pause
