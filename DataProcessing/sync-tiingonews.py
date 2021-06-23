#!/usr/bin/env python3

import os
import pandas as pd
import requests
from datetime import datetime
from pathlib import Path
from urllib.request import urlretrieve
from urllib.error import HTTPError


TIINGO_API_KEY=os.environ["TIINGO_API_KEY"]

known_missing_dates = ['bulkfile_2017-03-11_2017-03-13.tar.gz',
                       'bulkfile_2018-03-10_2018-03-12.tar.gz']

raw_data_folder = Path('/raw/alternative/tiingo')
raw_data_folder.mkdir(parents=True, exist_ok=True)

print("Download bulk files list...")
headers = { 'Content-Type': 'application/json' }
requestResponse = requests.get(f"https://api.tiingo.com/tiingo/news/bulk_download?token={TIINGO_API_KEY}", headers=headers)

df = pd.DataFrame(requestResponse.json())
df.set_index('id', drop=True, inplace=True)
df.sort_index(ascending=False)
# Get only incremental files.
df = df[df.batchType=='incremental']
# In case of duplicate, keep the last one
df = df[~df.filename.duplicated(keep='first')]

if "QC_DATAFLEET_DEPLOYMENT_DATE" in os.environ:
    # Datetime normalization and selection of data between range
    processing_date = datetime.strptime(os.environ["QC_DATAFLEET_DEPLOYMENT_DATE"], "%Y%m%d").date()

    df['startDate'] = pd.to_datetime(df['startDate'], utc=True).dt.date
    df['endDate'] = pd.to_datetime(df['endDate'], utc=True).dt.date
    df = df.query('startDate <= @processing_date <= endDate')

print("Look for bulk files in our local raw folder...")
present_files = [f.name for f in raw_data_folder.glob('*.tar.gz')]
files_to_download = list(set(df.filename) - set(present_files) - set(known_missing_dates))
print(f"Starting download {len(files_to_download)} files...")

for bulk_file in files_to_download:
    row = df[df.filename==bulk_file]
    url = row.url.values[0]
    file_name = row.filename.values[0]
    destination_path = raw_data_folder / file_name
    print (f'Download {row.filename.values[0]} to {raw_data_folder.resolve()}')
    try:
        urlretrieve(url, destination_path)
    except HTTPError as e:
        print(f"URL {url} not found!", e)
        continue
    

print("Done!")