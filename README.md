SimpleSyndicator gets images, gifs, and MP4s from Reddit (via RSS or JSON API, not HTML scraping) and posts them to Telegram.

Galleries of multiple images are supported, and each image is posted with a link back to Reddit for source / credits.

Extensible to allow new sources / new destinations easily.

Sample config.json (in .gitignore for this repo so I don't accidentally upload it!)
```
{
  "Sources": [
    {
      "Type": "Reddit",
      "URL": "https://www.reddit.com/r/beachpron/new.json",
      "Tags": "#beach #relaxation"
    },
    {
      "Type": "Reddit",
      "URL": "https://www.reddit.com/r/cats/search.json?q=flair%3ACat%20Picture%20-%20OC\u0026restrict_sr=on\u0026include_over_18=on\u0026sort=new\u0026t=day",
      "Tags": "#cats"
    }
  ],
  "TelegramKey": "111222333",
  "TelegramChannelIDs": [
    -123456789
  ],
  "IFTTTWebHooks": [
  ],
  "SyndicatedURLs": [
  ]
}
```
