using System;
using System.Collections.Generic;
using andywiecko.BurstTriangulator;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
using UnityEngine;
using static andywiecko.BurstTriangulator.Utilities;


/**
 * This class generates a sphere mesh using the Fibonacci sphere algorithm and Delaunay triangulation.
 * It also includes methods for stereographic projection of points between 2D and 3D. 
 * Then it uses perlin noise to generate some realistic terrain on the sphere.
 * 
 * TODO: I want to tile the sphere, but am confused about the centroid calculation aspect. Will add later.
 * 
 * 
 */
public class FibbSpherePlanet : MonoBehaviour
{

    List<Vector3> points = new List<Vector3>();
    List<Color> colours = new List<Color>();
    int[] triangles;
    MeshFilter mf;
    int poleIndex;
    [SerializeField] float jitter = 0;
    // Sea Level
    [SerializeField] float seaLevel;
    // Planet radius in world units, also used in pnoise.
    [SerializeField] float radius = 1.0f;
    // This is the scale of how high the mountains can go, it shouldn't go very high
    // (especially over 1), as it makes the peaks and valleys to eccentric. This is relative to the radius.
    // higher numba -> more cartoony mountains and valleys.
    [SerializeField] float extremaScale = 0.1f;
    // perlin noise frequency and amplitude
    [Header("Perlin Noise Stuff")]
    [SerializeField] float frequency1 = 1.0f;
    [SerializeField] float frequency2 = 1.0f;
    [SerializeField] float frequency3 = 1.0f;
    [SerializeField] float frequency4 = 1.0f;
    [SerializeField] float frequency5 = 1.0f;
    [SerializeField] float frequency6 = 1.0f;
    [SerializeField] float amplitude1 = 0.1f;
    [SerializeField] float amplitude2 = 0.1f;
    [SerializeField] float amplitude3 = 0.1f;
    [SerializeField] float amplitude4 = 0.1f;
    [SerializeField] float amplitude5 = 0.1f;
    [SerializeField] float amplitude6 = 0.1f;

    List<float> frequencyList = new List<float>();
    List<float> amplitudeList = new List<float>();

    // How many verticies should we use?
    [SerializeField] int vertexStartCount = 8000;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Debug.Log("FibbSpherePlanet script has started.");
        Debug.Log(gameObject.name);
        // Collect the coeffs into an easy to manipulate list.
        frequencyList.Add(frequency1);
        frequencyList.Add(frequency2);
        frequencyList.Add(frequency3);
        frequencyList.Add(frequency4);
        frequencyList.Add(frequency5);
        frequencyList.Add(frequency6);
        amplitudeList.Add(amplitude1);
        amplitudeList.Add(amplitude2);
        amplitudeList.Add(amplitude3);
        amplitudeList.Add(amplitude4);
        amplitudeList.Add(amplitude5);
        amplitudeList.Add(amplitude6);

        // Mesh (filter) that the GO is attached to.
        mf = GetComponent<MeshFilter>();
        // trying this out hehe
        mf.mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;


        // So I realized that sphere not going to work. instead what we have to do is generate a point with FibbSpherePlanet and then use delaunay triangulation to generate a mesh from that. Then we can use the diamond square algorithm on that mesh.
        poleIndex = vertexStartCount;
        points = FibbSphere(vertexStartCount);


        // new issue: 3D delaunay is not implementened. Redblob recommends stereographic projection to 2d and go from there, then project back to 3D.
        // This is good, because it means that in the middle step I can run some terrain generation.
        // ungodly poor memory management.
        Vector2[] projectedPoints = StereographicProject2D(points.ToArray());

        using NativeArray<Vector2> projectedPointsNative = new NativeArray<Vector2>(projectedPoints, Allocator.Persistent);

        using var triangulator = new Triangulator<Vector2>(Allocator.Persistent)
        {
            Input = { Positions = projectedPointsNative },
            Settings = { AutoHolesAndBoundary = true }
        };
        triangulator.Run();

        // fill in the hole
        List<int> trangles = new List<int>(triangulator.Output.Triangles.AsArray().ToArray());
        NativeList<int> halfEdges = triangulator.Output.Halfedges;
        int originalCount = trangles.Count;

        // this is the pole point, we're actually gonna change it to be mean later.
        points.Add(new Vector3(0, 0, 1));


        //// store for average
        Vector3 avg = new Vector3(0, 0, 0);
        ushort count = 0;
        List<int> halfEdgeInds = new List<int>(6);
        for (int i = 0; i < halfEdges.Length; i++)
        {
            // we only want to add a trangle on the edge of the hole. skip the ones that are sharing a half edge with another.
            if (halfEdges[i] != -1) continue;
            halfEdgeInds.Add(i);

            count++;
            int a = trangles[i];
            int b = trangles[NextHalfedge(i)];

            avg += points[a];

            trangles.Add(b);
            trangles.Add(a);
            trangles.Add(poleIndex);

        }
        // So the vertex we added might be a little off from the hole. so we take the avg. of the (x,y).
        avg /= count;
        points[poleIndex] = new Vector3(avg.x, avg.y, math.sqrt(1 - math.square(avg.x) - math.square(avg.y)));

        triangles = triangulator.Output.Triangles.AsArray().ToArray();

        Debug.Log("Number of halfedges in total: " + string.Join(", ", halfEdges.Length));

        ApplyPerlinNoise();

        mf.mesh.SetVertices(points);
        mf.mesh.SetTriangles(trangles, 0);
        mf.mesh.SetColors(colours);
        // annnnd finally rotate the mesh so that we see the pretty piece thats at the n pole.
        // Explanation: I didn't realize unity has +z coming out of the screen.
        gameObject.transform.rotation = Quaternion.Euler(-90, 0, 0);
        mf.mesh.RecalculateNormals();
    }

    private void ApplyPerlinNoise()
    {
        // We could add perlin noise in 2d and stereo project back to 3d, but I think just iterating through again is fine
        // check out this: https://www.redblobgames.com/maps/terrain-from-noise/
        for (int i = 0; i < points.Count; i++)
        {
            // so we're gonna do the layers that redblob does, and see how that works out.
            // Layering works simply, apply perlin (cnoise) with a couple different
            float3 pf = points[i];


            float pnoise = 0;
            float ampSum = 0;

            for(int j = 0; j < frequencyList.Count; j++)
            {
                pnoise += amplitudeList[j] * noise.cnoise(pf * frequencyList[j]);
                ampSum += amplitudeList[j];
            }
            pnoise /= ampSum;
            // redblob uses a fudge factor and exponent to push pull valleys.
            // New issue: cnoise gives a [-1,1]. This is not good for powers. If we add 1 we just get [0,2].
            // so instead we do add 1 divide by 2. [-1,1] -> [0,2] -> [0,1]
            pnoise = math.saturate((pnoise + 1) * 0.5f);
            pnoise = math.pow(pnoise*1.2f, 1.9f);

            float height = math.max(pnoise, seaLevel);
            height = 1f + height * extremaScale;
            // if height (which is more like a vertical scale factor) is below .6 (i'll serialize later) it's gonna count as water, and we just keep it at .6
            points[i] = math.normalize(pf) * (radius * height);

            // finally, add some colour (just simple for now).
            SimpleVertexColour(i, pnoise);


        }
    }

    private void SimpleVertexColour(int i, float pnoise)
    {
        if (pnoise <= seaLevel) colours.Add(Color.darkBlue);
        // magic number in process of figuring out what height looks good for snow.
        else if(pnoise >= 0.8) colours.Add(Color.snow);
        else colours.Add(Color.forestGreen);
    }

    /**
     * Stereographic projection of 3D points onto a 2D plane. Will at some point allow you to specify a different unit sphere radius,
     * but I don't know if thats even necessary.
     * This method is not implemented yet.
     *
     * @param cgalPoints The array of 3D points to project.
     * @return An array of 2D points resulting from the projection.
     */
    private Vector2[] StereographicProject2D(Vector3[] points3d)
    {
        Vector2[] planePoints = new Vector2[points3d.Length];
        for (int i = 0; i < points3d.Length; i++)
        {
            Vector3 p = points3d[i];

            double x = p.x / (1 - p.z);
            double y = p.y / (1 - p.z);

            planePoints[i] = new Vector2((float)x, (float)y);

        }
        return planePoints;
    }

    /**
     * Stereographic projection of 2D points onto a 3D unit sphere. Will at some point allow you to specify a different unit sphere radius,
     * but I don't know if thats even necessary.
     * This method is not implemented yet.
     *
     * @param points2d The array of 2D points to project.
     * @return An array of 3D points resulting from the projection.
     */
    private Vector3[] StereographicProject3D(Vector2[] points2d)
    {
        Vector3[] spherePoints = new Vector3[points2d.Length];

        double px;
        double py;

        double xySquared;
        for (int i = 0; i < points2d.Length; i++)
        {
            px = points2d[i].x;
            py = points2d[i].y;
            xySquared = math.square(px) + math.square(py);

            double x = 2 * px / (1 + xySquared);
            double y = 2 * py / (1 + xySquared);
            double z = (-1 + xySquared) / (1 + xySquared);

            spherePoints[i] = new Vector3((float)x, (float)y, (float)z);
        }
        return spherePoints;
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    // https://www.redblobgames.com/x/1842-delaunay-voronoi-sphere/
    // paper its based on https://arxiv.org/pdf/0912.4540
    // seems easiser that the subdivided icosahedron
    private List<Vector3> FibbSphere(int numPoints)
    {

        //CGALDotNetGeometry.Numerics.Point3d[numPoints] pointArray = new Vector3[numPoints];
        List<Vector3> pointArray = new List<Vector3>(numPoints);
        // yoinked, why do we need this? it's apparently the golden angle
        double phi = math.PI_DBL * (math.sqrt(5.0) - 1.0);

        for ( int i = 0; i < numPoints; i++)
        {
            // y coord
            double y = 1 - ((double)i / (numPoints - 1)) * 2;
            // radius from the center at this point.
            double radius = math.sqrt(1 - y * y);

            double theta = phi * i;

            double x = math.cos(theta) * radius;
            double z = math.sin(theta) * radius;


            pointArray.Add(new Vector3((float)x, (float)y, (float)z));

        }
        return pointArray;
    }
}
