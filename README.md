# Barikada
A blocklist of Philippine gambling (or scatter) sites for uBlock Origin, preventing potential
phishing or malicious attacks and addiction, even when attempt to proceed.

This blocklist works on Desktop and Mobile using uBlock Origin, or uBlacklist extension, so make sure
you installed either extensions on your browser if preferred.

A hosts file can be used to utilize DNS blocking to sites, rendering them offline.

## Importing blocklist on uBlock Origin (Desktop)
1. Click the uBlock Origin Extension, and click the Dashboard (Cog-wheel icon) on the bottom right.
2. Go to Filter Lists tab > Import, and expand it.
3. Copy the Link below, and paste into text box
   ```
   https://raw.githubusercontent.com/PheeLeep/Barikada/main/anti_scatter.txt
   ```
4.  Click Apply Changes, and you're done!

## Importing blocklist on uBlock Origin Mobile
(must have uBlock Origin installed on FireFox mobile, or its forked version IronFox)

1. Go to Options Button (three-dot vertical) > Extensions > uBlock Origin
2. Click the Open the Dashboard (Cog-wheel icon) on the bottom right.
3. Go to Filter Lists tab > Import, and expand it.
4. Copy the Link below, and paste into the text box
   ```
   https://raw.githubusercontent.com/PheeLeep/Barikada/main/anti_scatter.txt
   ```
5.  Click Apply Changes, and you're done!

uBlock Origin will automatically refresh the filter list once a day. To force an update, click
the Clock icon right next to "PheeLeep's Barikada Anti-Scatter List".

## Importing blocklist on uBlacklist
1. Go to Options button
2. *Enable "Other search engines"*. This ensures that the blocklist will work on many other search engines
3. Scroll to the bottom and go to Subscription.
4. Go to Add Subscription, in the URL box, paste the following:
   ```
   https://raw.githubusercontent.com/PheeLeep/Barikada/main/ublacklist_antiscatter.txt
   ```
5. (Optional) Add the name of the blocklist to the "Alternative Name".
6. Click Add, and you're done!

## Importing blocklist automatically via uBlacklist
If you have any Chromium-based browsers (Chrome, Brave, Edge, etc.). then click on [this link](https://iorate.github.io/ublacklist/subscribe?name=Barikada&url=https://raw.githubusercontent.com/PheeLeep/Barikada/main/ublacklist_antiscatter.txt) to subscribe automatically to the blocklist.

## Importing hosts file
Insert the file through a preferred DNS blocking system (like PiHole or Adguard.)

### For pi-hole:
1. Visit your admin's dashboard
2. Click on Adlists
3. Copy and paste the url into the address: box
4. Hit the add button, and it should be added.

### For Adguard:
1. Open Adguard Home Dashboard
2. Go to filters --> DNS blocklists.
3. Click Add blocklist, then Add a custom list.
4. Enter the name of the list (eg. AI blocklist) into the first dialogue box.
5. Copy and paste the url into the second dialogue box.
6. Hit save, and the list is added!

### System-wide:
You can also append the contents of the hosts file through your system's as well.

Locations for the hosts file are as follows:
- **Windows**: `%SystemRoot%\System32\drivers\etc\hosts`
- **macOS/Linux/Unix**: `/etc/hosts`

## Contribution
If you found scatter sites promoted by vloggers on social media sites, or spammed thru SMS messages, 
you can make a Pull Request to contribute the project and add link to it.

## Proof

- This is an image before importing the blocklist:
![Old Screenshot](https://github.com/PheeLeep/Barikada/blob/main/images/screenshot_old.png)

- This is an image after import:
![New Screenshot](https://github.com/PheeLeep/Barikada/blob/main/images/screenshot_new.png)

- This is an image when a user click Proceed on uBlock Origin:
![New Screenshot](https://github.com/PheeLeep/Barikada/blob/main/images/screenshot_new_blank.png)
