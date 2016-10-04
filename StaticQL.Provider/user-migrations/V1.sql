create table Users
       ( Id int not null primary key
       , Name string(128)
       , Email string(128)
       , Password binary(64)
       , Salt binary(64)
       )

create Table Groups
       ( Id not null primary key
       , Name string(128)
       )

create table UserGroupMaps
       ( UserId primary key references Users(Id)
       , GroupId primary key references Groups(Id)
       )

