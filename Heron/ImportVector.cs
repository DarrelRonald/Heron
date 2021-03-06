﻿using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using OSGeo.OSR;
using OSGeo.OGR;
using Rhino.Geometry;
using System;
using System.Collections.Generic;



namespace Heron
{
    public class ImportVector : HeronComponent
    {
        //Class Constructor
        public ImportVector() : base("Import Vector", "ImportVector", "Import vector GIS data clipped to a boundary, including SHP, GeoJSON, OSM, KML, MVT and GDB folders.", "GIS Tools")
        {

        }


        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Boundary", "boundary", "Boundary curve(s) for vector data", GH_ParamAccess.list);
            pManager.AddTextParameter("Vector Data Location", "fileLoc", "File path for the vector data input", GH_ParamAccess.item);
            pManager.AddTextParameter("User Spatial Reference System", "userSRS", "Custom SRS", GH_ParamAccess.item, "WGS84");
            pManager.AddBooleanParameter("Crop file", "cropIt", "Crop the file to boundary?", GH_ParamAccess.item, true);
            pManager[0].Optional = true;
            pManager[2].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Extents", "extents", "Bounding box of all the vector data features", GH_ParamAccess.tree);
            pManager.AddTextParameter("Source Spatial Reference System", "sourceSRS", "Spatial Reference of the input vector data", GH_ParamAccess.tree);
            pManager.AddTextParameter("Fields", "fields", "Fields of data associated with the vector data features", GH_ParamAccess.tree);
            pManager.AddTextParameter("Values", "values", "Field values for each feature", GH_ParamAccess.tree);
            pManager.AddPointParameter("FeaturePoints", "featurePoints", "Point geometry describing each feature", GH_ParamAccess.tree);

            pManager.AddPointParameter("FeaturePointsUser", "featurePointsUser", "Point geometry describing each feature", GH_ParamAccess.tree);
            pManager.AddCurveParameter("ExtentUser", "extentsUser", "Bounding box of all Shapefile features given the userSRS", GH_ParamAccess.tree);

            pManager.AddGeometryParameter("Feature Geometry", "featureGeomery", "Geometry contained in the feature", GH_ParamAccess.tree);
            pManager.AddTextParameter("Geometry Type", "geoType", "Type of geometry contained in the feature", GH_ParamAccess.tree);

        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {

            ///Gather GHA inputs
            List<Curve> boundary = new List<Curve>();
            DA.GetDataList<Curve>("Boundary", boundary);

            string shpFileLoc = "";
            DA.GetData<string>("Vector Data Location", ref shpFileLoc);


            bool cropIt = true;
            DA.GetData<Boolean>("Crop file", ref cropIt);

            string userSRStext = "WGS84";
            DA.GetData<string>(2, ref userSRStext);


            ///GDAL setup
            ///Some preliminary testing has been done to read SHP, GeoJSON, OSM, KML, MVT, GML and GDB
            ///It can be spotty with KML, MVT and GML and doesn't throw informative errors.  Likely has to do with getting a valid CRS and 
            ///TODO: resolve errors with reading KML, MVT, GML.

            RESTful.GdalConfiguration.ConfigureOgr();
            OSGeo.OGR.Ogr.RegisterAll();
            OSGeo.OGR.Driver drv = OSGeo.OGR.Ogr.GetDriverByName("ESRI Shapefile");
            OSGeo.OGR.DataSource ds = OSGeo.OGR.Ogr.Open(shpFileLoc, 0);

            if (ds == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The vector datasource was unreadable by this component. It may not a valid file type for this component or otherwise null/empty.");
                return;
            }

            List<OSGeo.OGR.Layer> layerset = new List<OSGeo.OGR.Layer>();
            List<int> fc = new List<int>();

            for (int iLayer = 0; iLayer < ds.GetLayerCount(); iLayer++)
            {
                OSGeo.OGR.Layer layer = ds.GetLayerByIndex(iLayer);

                if (layer == null)
                {
                    Console.WriteLine("Couldn't fetch advertised layer " + iLayer);
                    System.Environment.Exit(-1);
                }
                else
                {
                    layerset.Add(layer);
                }
            }

            ///Declare trees
            GH_Structure<GH_Rectangle> recs = new GH_Structure<GH_Rectangle>();
            GH_Structure<GH_String> sRefs = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fnames = new GH_Structure<GH_String>();
            GH_Structure<GH_String> fset = new GH_Structure<GH_String>();
            GH_Structure<GH_Point> gset = new GH_Structure<GH_Point>();

            GH_Structure<GH_Point> gsetUser = new GH_Structure<GH_Point>();
            GH_Structure<GH_Rectangle> recsUser = new GH_Structure<GH_Rectangle>();

            GH_Structure<GH_String> gtype = new GH_Structure<GH_String>();
            GH_Structure<IGH_GeometricGoo> gGoo = new GH_Structure<IGH_GeometricGoo>();


            ///Loop through each layer. Layers usually occur in Geodatabase GDB format. SHP usually has only one layer.
            for (int iLayer = 0; iLayer < ds.GetLayerCount(); iLayer++)
            {
                OSGeo.OGR.Layer layer = ds.GetLayerByIndex(iLayer);

                if (layer == null)
                {
                    Console.WriteLine("Couldn't fetch advertised layer " + iLayer);
                    System.Environment.Exit(-1);
                }

                long count = layer.GetFeatureCount(1);
                int featureCount = System.Convert.ToInt32(count);

                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Layer #" + iLayer + " " + layer.GetName() + " has " + featureCount + " features");

                ///Get the spatial reference of the input vector file and set to WGS84 if not known
                OSGeo.OSR.SpatialReference sourceSRS = new SpatialReference(Osr.SRS_WKT_WGS84);
                string sRef = "";

                if (layer.GetSpatialRef() == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) is missing.  SRS set automatically set to WGS84.");
                    Driver driver = ds.GetDriver();
                    if (driver.GetName() == "MVT") { sourceSRS.SetFromUserInput("EPSG:3857"); }
                    else { sourceSRS.SetFromUserInput("WGS84"); } ///this seems to work where SetWellKnownGeogCS doesn't

                    string pretty = "";
                    sourceSRS.ExportToPrettyWkt(out pretty, 0);
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, pretty);
                    sRef = "Spatial Reference System (SRS) is missing.  SRS set automatically set to WGS84.";
                }

                else
                {
                    if (layer.GetSpatialRef().Validate() != 0)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Spatial Reference System (SRS) is unknown or unsupported.  SRS set automatically set to WGS84.");
                        sourceSRS.SetWellKnownGeogCS("WGS84");
                        sRef = "Spatial Reference System (SRS) is unknown or unsupported.  SRS set automatically set to WGS84.";
                    }
                    else
                    {
                        sourceSRS = layer.GetSpatialRef();
                        sourceSRS.ExportToWkt(out sRef);
                        try
                        {
                            int sourceSRSInt = Int16.Parse(sourceSRS.GetAuthorityCode(null));
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "The source Spatial Reference System (SRS) from layer " + layer.GetName() + " is EPSG:" + sourceSRSInt + ".");
                        }
                        catch
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Failed to get an EPSG Spatial Reference System (SRS) from layer " + layer.GetName() + ".");
                        }
                    }

                }

                sRefs.Append(new GH_String(sRef), new GH_Path(iLayer));


                ///Set transform from input spatial reference to Rhino spatial reference
                ///TODO: look into adding a step for transforming to CRS set in SetCRS 
                OSGeo.OSR.SpatialReference rhinoSRS = new OSGeo.OSR.SpatialReference("");
                rhinoSRS.SetWellKnownGeogCS("WGS84");

                ///TODO: verify the userSRS is valid
                ///TODO: use this as override of global SetSRS
                OSGeo.OSR.SpatialReference userSRS = new OSGeo.OSR.SpatialReference("");
                userSRS.SetFromUserInput(userSRStext);

                ///These transforms move and scale in order to go from userSRS to XYZ and vice versa
                Transform userSRSToModelTransform = Heron.Convert.GetUserSRSToModelTransform(userSRS);
                Transform modelToUserSRSTransform = Heron.Convert.GetModelToUserSRSTransform(userSRS);
                Transform sourceToModelSRSTransform = Heron.Convert.GetUserSRSToModelTransform(sourceSRS);
                Transform modelToSourceSRSTransform = Heron.Convert.GetModelToUserSRSTransform(sourceSRS);

                ///Get OGR envelope of the data in the layer in the sourceSRS
                OSGeo.OGR.Envelope ext = new OSGeo.OGR.Envelope();
                layer.GetExtent(ext, 1);

                OSGeo.OGR.Geometry extMinSourceOgr = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
                extMinSourceOgr.AddPoint(ext.MinX, ext.MinY, 0.0);
                extMinSourceOgr.AssignSpatialReference(sourceSRS);

                OSGeo.OGR.Geometry extMaxSourceOgr = new OSGeo.OGR.Geometry(wkbGeometryType.wkbPoint);
                extMaxSourceOgr.AddPoint(ext.MaxX, ext.MaxY, 0.0);
                extMaxSourceOgr.AssignSpatialReference(sourceSRS);

                ///Get extents in Rhino SRS
                Point3d extPTmin = Heron.Convert.OgrPointToPoint3d(extMinSourceOgr, sourceToModelSRSTransform);
                Point3d extPTmax = Heron.Convert.OgrPointToPoint3d(extMaxSourceOgr, sourceToModelSRSTransform);

                Rectangle3d rec = new Rectangle3d(Plane.WorldXY, extPTmin, extPTmax);
                recs.Append(new GH_Rectangle(rec), new GH_Path(iLayer));

                ///Get extents in userSRS
                ///Can give odd results if crosses 180 longitude
                extMinSourceOgr.TransformTo(userSRS);
                extMaxSourceOgr.TransformTo(userSRS);

                Point3d extPTminUser = Heron.Convert.OgrPointToPoint3d(extMinSourceOgr, userSRSToModelTransform);
                Point3d extPTmaxUser = Heron.Convert.OgrPointToPoint3d(extMaxSourceOgr, userSRSToModelTransform);

                Rectangle3d recUser = new Rectangle3d(Plane.WorldXY, extPTminUser, extPTmaxUser);
                recsUser.Append(new GH_Rectangle(recUser), new GH_Path(iLayer));


                if (boundary.Count == 0 && cropIt == true)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Define a boundary or set cropIt to False");
                }

                else if (boundary.Count == 0 && cropIt == false)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Clipping boundary has not been defined. File extents will be used instead");
                    boundary.Add(rec.ToNurbsCurve());
                }



                ///Loop through input boundaries
                for (int i = 0; i < boundary.Count; i++)
                {
                    OSGeo.OGR.FeatureDefn def = layer.GetLayerDefn();

                    ///Get the field names
                    List<string> fieldnames = new List<string>();
                    for (int iAttr = 0; iAttr < def.GetFieldCount(); iAttr++)
                    {
                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iAttr);
                        fnames.Append(new GH_String(fdef.GetNameRef()), new GH_Path(i, iLayer));
                    }

                    ///Check if boundary is contained in extent
                    if (!rec.IsValid || ((rec.Height == 0) && (rec.Width == 0)))
                    {
                        ///Get field data if even if no geometry is present in the layer
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more vector datasource bounds are not valid.");
                        OSGeo.OGR.Feature feat;
                        int m = 0;

                        while ((feat = layer.GetNextFeature()) != null)
                        {
                            ///Loop through field values
                            for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                            {
                                OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, iLayer, m));
                                fdef.Dispose();
                            }
                            m++;
                            feat.Dispose();
                        }
                    }

                    else if (boundary[i] == null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clipping boundary " + i + " not set.");

                    else if (!boundary[i].IsValid) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clipping boundary " + i + "  is not valid.");

                    else if (rec.IsValid && Curve.PlanarClosedCurveRelationship(rec.ToNurbsCurve(), boundary[i], Plane.WorldXY, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance) == RegionContainment.Disjoint)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "One or more clipping boundaries may be outside the bounds of the vector datasource.");
                    }

                    else
                    {
                        ///Create bounding box for clipping geometry
                        Point3d min = boundary[i].GetBoundingBox(true).Min;
                        Point3d max = boundary[i].GetBoundingBox(true).Max;
                        min.Transform(modelToSourceSRSTransform);
                        max.Transform(modelToSourceSRSTransform);
                        double[] minpT = new double[3];
                        double[] maxpT = new double[3];

                        minpT[0] = min.X;
                        minpT[1] = min.Y;
                        minpT[2] = min.Z;
                        maxpT[0] = max.X;
                        maxpT[1] = max.Y;
                        maxpT[2] = max.Z;


                        OSGeo.OGR.Geometry ebbox = OSGeo.OGR.Geometry.CreateFromWkt("POLYGON((" + minpT[0] + " " + minpT[1] + ", " + minpT[0] + " " + maxpT[1] + ", " + maxpT[0] + " " + maxpT[1] + ", " + maxpT[0] + " " + minpT[1] + ", " + minpT[0] + " " + minpT[1] + "))");

                        ///Create bounding box for clipping geometry
                        ///Not working on MVT type files
                        //boundary[i].Transform(modelToSourceSRSTransform);
                        //OSGeo.OGR.Geometry ebbox = Heron.Convert.CurveToOgrPolygon(boundary[i]);


                        ///Clip Shapefile
                        ///http://pcjericks.github.io/py-gdalogr-cookbook/vector_layers.html
                        OSGeo.OGR.Layer clipped_layer = layer;

                        if (cropIt)
                        {
                            clipped_layer.SetSpatialFilter(ebbox);
                        }

                        ///Loop through geometry
                        OSGeo.OGR.Feature feat;
                        def = clipped_layer.GetLayerDefn();

                        int m = 0;
                        while ((feat = clipped_layer.GetNextFeature()) != null)
                        {

                            OSGeo.OGR.Geometry geom = feat.GetGeometryRef();
                            OSGeo.OGR.Geometry sub_geom;

                            OSGeo.OGR.Geometry geomUser = feat.GetGeometryRef().Clone();
                            OSGeo.OGR.Geometry sub_geomUser;

                            ///reproject geometry to WGS84 and userSRS
                            ///TODO: look into using the SetCRS global variable here
                            if (geom.GetSpatialReference() == null) { geom.AssignSpatialReference(sourceSRS); }
                            if (geomUser.GetSpatialReference() == null) { geomUser.AssignSpatialReference(sourceSRS); }

                            geom.TransformTo(rhinoSRS);
                            geomUser.TransformTo(userSRS);
                            gtype.Append(new GH_String(geom.GetGeometryName()), new GH_Path(i, iLayer, m));

                            if (feat.GetGeometryRef() != null)
                            {

                                ///Convert GDAL geometries to IGH_GeometricGoo
                                gGoo.AppendRange(Heron.Convert.OgrGeomToGHGoo(geomUser, userSRSToModelTransform), new GH_Path(i, iLayer, m));

                                /// Get Feature Values
                                if (fset.PathExists(new GH_Path(i, iLayer, m)))
                                {
                                    fset.get_Branch(new GH_Path(i, iLayer, m)).Clear();
                                }
                                for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                {
                                    OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                    if (feat.IsFieldSet(iField))
                                    {
                                        fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, iLayer, m));
                                    }
                                    else
                                    {
                                        fset.Append(new GH_String("null"), new GH_Path(i, iLayer, m));
                                    }
                                }
                                ///End get Feature Values


                                ///Start get points if open polylines and points
                                for (int gpc = 0; gpc < geom.GetPointCount(); gpc++)
                                {
                                    ///Loop through geometry points for Rhino SRS
                                    double[] ogrPt = new double[3];
                                    geom.GetPoint(gpc, ogrPt);
                                    Point3d pt3D = new Point3d(ogrPt[0], ogrPt[1], ogrPt[2]);
                                    pt3D.Transform(Heron.Convert.WGSToXYZTransform());

                                    gset.Append(new GH_Point(pt3D), new GH_Path(i, iLayer, m));

                                    ///Loop through geometry points for User SRS
                                    double[] ogrPtUser = new double[3];
                                    geomUser.GetPoint(gpc, ogrPtUser);
                                    Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                    pt3DUser.Transform(userSRSToModelTransform);

                                    gsetUser.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m));

                                    ///End loop through geometry points


                                    /// Get Feature Values
                                    if (fset.PathExists(new GH_Path(i, iLayer, m)))
                                    {
                                        fset.get_Branch(new GH_Path(i, iLayer, m)).Clear();
                                    }
                                    for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                    {
                                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                        if (feat.IsFieldSet(iField))
                                        {
                                            fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, iLayer, m));
                                        }
                                        else
                                        {
                                            fset.Append(new GH_String("null"), new GH_Path(i, iLayer, m));
                                        }
                                    }
                                    ///End Get Feature Values
                                }
                                ///End getting points if open polylines or points


                                ///Start getting points if closed polylines and multipolygons
                                for (int gi = 0; gi < geom.GetGeometryCount(); gi++)
                                {
                                    sub_geom = geom.GetGeometryRef(gi);
                                    OSGeo.OGR.Geometry subsub_geom;

                                    sub_geomUser = geomUser.GetGeometryRef(gi);
                                    OSGeo.OGR.Geometry subsub_geomUser;

                                    if (sub_geom.GetGeometryCount() > 0)
                                    {
                                        for (int n = 0; n < sub_geom.GetGeometryCount(); n++)
                                        {

                                            subsub_geom = sub_geom.GetGeometryRef(n);
                                            subsub_geomUser = sub_geomUser.GetGeometryRef(n);

                                            for (int ptnum = 0; ptnum < subsub_geom.GetPointCount(); ptnum++)
                                            {
                                                ///Loop through geometry points
                                                double[] ogrPt = new double[3];
                                                subsub_geom.GetPoint(ptnum, ogrPt);
                                                Point3d pt3D = new Point3d(ogrPt[0], ogrPt[1], ogrPt[2]);
                                                pt3D.Transform(Heron.Convert.WGSToXYZTransform());

                                                gset.Append(new GH_Point(pt3D), new GH_Path(i, iLayer, m, gi, n));

                                                ///Loop through geometry points for User SRS
                                                double[] ogrPtUser = new double[3];
                                                subsub_geomUser.GetPoint(ptnum, ogrPtUser);
                                                Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                                pt3DUser.Transform(userSRSToModelTransform);

                                                gsetUser.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m, gi, n));

                                                ///End loop through geometry points
                                            }
                                            subsub_geom.Dispose();
                                            subsub_geomUser.Dispose();
                                        }
                                    }

                                    else
                                    {
                                        for (int ptnum = 0; ptnum < sub_geom.GetPointCount(); ptnum++)
                                        {
                                            ///Loop through geometry points
                                            double[] ogrPt = new double[3];
                                            sub_geom.GetPoint(ptnum, ogrPt);
                                            Point3d pt3D = new Point3d(ogrPt[0], ogrPt[1], ogrPt[2]);
                                            pt3D.Transform(Heron.Convert.WGSToXYZTransform());

                                            gset.Append(new GH_Point(pt3D), new GH_Path(i, iLayer, m, gi));

                                            ///Loop through geometry points for User SRS
                                            double[] ogrPtUser = new double[3];
                                            sub_geomUser.GetPoint(ptnum, ogrPtUser);
                                            Point3d pt3DUser = new Point3d(ogrPtUser[0], ogrPtUser[1], ogrPtUser[2]);
                                            pt3DUser.Transform(userSRSToModelTransform);

                                            gsetUser.Append(new GH_Point(pt3DUser), new GH_Path(i, iLayer, m, gi));

                                            ///End loop through geometry points
                                        }
                                    }

                                    sub_geom.Dispose();
                                    sub_geomUser.Dispose();


                                    /// Get Feature Values
                                    if (fset.PathExists(new GH_Path(i, iLayer, m)))
                                    {
                                        fset.get_Branch(new GH_Path(i, iLayer, m)).Clear();
                                    }
                                    for (int iField = 0; iField < feat.GetFieldCount(); iField++)
                                    {
                                        OSGeo.OGR.FieldDefn fdef = def.GetFieldDefn(iField);
                                        if (feat.IsFieldSet(iField))
                                        {
                                            fset.Append(new GH_String(feat.GetFieldAsString(iField)), new GH_Path(i, iLayer, m));
                                        }
                                        else
                                        {
                                            fset.Append(new GH_String("null"), new GH_Path(i, iLayer, m));
                                        }
                                    }
                                    ///End Get Feature Values


                                }


                                //m++;

                            }
                            m++;
                            geom.Dispose();
                            geomUser.Dispose();
                            feat.Dispose();
                        }///end while loop through features

                    }///end clipped layer else statement

                }///end loop through boundaries

                layer.Dispose();

            }///end loop through layers

            ds.Dispose();

            DA.SetDataTree(0, recs);
            DA.SetDataTree(1, sRefs);
            DA.SetDataTree(2, fnames);
            DA.SetDataTree(3, fset);
            DA.SetDataTree(4, gset);

            DA.SetDataTree(5, gsetUser);
            DA.SetDataTree(6, recsUser);

            DA.SetDataTree(7, gGoo);
            DA.SetDataTree(8, gtype);
        }


        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Properties.Resources.shp;
            }
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("{CCDA0ABF-ED36-4502-95EA-FD3024376F46}"); }
        }
    }
}
