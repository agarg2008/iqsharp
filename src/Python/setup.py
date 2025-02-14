#!/bin/env python
# -*- coding: utf-8 -*-
##
# setup.py: Installs Python host functionality for Q#.
##
# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License.
##

## IMPORTS ##

import setuptools
import os

## VERSION INFORMATION ##
# Our build process sets the PYTHON_VERSION environment variable to a version
# string that is compatible with PEP 440, and so we inherit that version number
# here and propagate that to qsharp/version.py.
#
# To make sure that local builds still work without the same environment
# variables, we'll default to 0.0.0.1 as a development version.

version = os.environ.get('PYTHON_VERSION', '0.0.0.1')

with open('./qsharp/version.py', 'w') as f:
    f.write(f'''# Auto-generated file, do not edit.
##
# version.py: Specifies the version of the qsharp package.
##
# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License.
##
__version__ = "{version}"
''')

## DESCRIPTION ##
# The long description metadata passed to setuptools is used to populate the
# PyPI page for this package. Thus, we'll generate the description by using the
# same README.md file that we use in the GitHub repo.

with open("./README.md", "r") as fh:
    long_description = fh.read()

## SETUPTOOLS INVOCATION ##

setuptools.setup(
    name="qsharp",
    version=version,
    author="Microsoft",
    description="Python client for Q#, a domain-specific quantum programming language",
    long_description=long_description,
    long_description_content_type="text/markdown",
    url="https://github.com/Quantum/QuantumLibraries",
    packages=setuptools.find_packages(),
    classifiers=[
        "Programming Language :: Python :: 3",
        "License :: OSI Approved :: MIT License",
        "Operating System :: OS Independent",
    ],
    install_requires=[
        'jupyter_client'
    ]
)
